<#
.SYNOPSIS
    Bootstrap script to deploy and start the Autopilot Monitor agent.

.DESCRIPTION
    This script is designed to be deployed via Intune as a PowerShell Script during Autopilot.
    It runs VERY EARLY in the enrollment process (first Intune action) and:
    1. Creates a unique session ID for this enrollment
    2. Downloads the monitoring agent binaries
    3. Installs agent binaries (agent uses built-in backend URL or CLI override)
    4. Registers agent as Scheduled Task (runs on computer startup)
    5. Agent self-destructs when enrollment completes

.PARAMETER AgentDownloadUrl
    URL to download the agent binaries from (ZIP file)

.EXAMPLE
    .\Install-AutopilotMonitor.ps1
    (Uses built-in backend URL from the agent)

.NOTES
    - Agent is temporary and auto-removes after enrollment
    - Everything in C:\ProgramData\AutopilotMonitor (easy cleanup)
    - No Registry entries (clean machine after removal)
    - Scheduled Task survives reboots during enrollment
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$AgentDownloadUrl = "https://autopilotmonitor.blob.core.windows.net/agent/AutopilotMonitor-Agent.zip",
    
    [Parameter(Mandatory = $false)]
    [int]$MaxOsAgeMinutes = 60
)

# Configuration - Everything in ProgramData for easy cleanup
$AgentBasePath = "$env:ProgramData\AutopilotMonitor"
$AgentBinPath = "$AgentBasePath\Agent"
$LogFile = "$AgentBasePath\Bootstrap.log"

# Ensure directories exist
New-Item -Path $AgentBasePath -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
New-Item -Path $AgentBinPath -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null

function Write-Log {
    param([string]$Message)
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $logMessage = "[$timestamp] $Message"
    Write-Output $logMessage
    Add-Content -Path $LogFile -Value $logMessage -ErrorAction SilentlyContinue
}

function Get-FileMd5Base64 {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $md5 = [System.Security.Cryptography.MD5]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($Path)
        try {
            $hashBytes = $md5.ComputeHash($stream)
        }
        finally {
            $stream.Dispose()
        }

        return [Convert]::ToBase64String($hashBytes)
    }
    finally {
        $md5.Dispose()
    }
}

try {
    Write-Log "===== Autopilot Monitor Bootstrap Started ====="

    # ── Pre-flight: Skip installation on already-enrolled devices ──

    # Check 1: OS install date must be within threshold (fresh device)
    $osInstallDate = (Get-CimInstance Win32_OperatingSystem).InstallDate
    $osAge = (Get-Date) - $osInstallDate
    $osAgeMinutesRounded = [int][Math]::Round($osAge.TotalMinutes)
    Write-Log "OS install date: $osInstallDate (age: ${osAgeMinutesRounded}m)"

    if ($osAge.TotalMinutes -gt $MaxOsAgeMinutes) {
        Write-Log "SKIP: OS was installed ${osAgeMinutesRounded}m ago (threshold: ${MaxOsAgeMinutes}m). Device is not freshly provisioned."
        exit 0
    }

    # Check 2: MDM enrollment must not be completed yet
    $enrollmentPath = "HKLM:\SOFTWARE\Microsoft\Enrollments"
    $mdmEnrolled = $false

    if (Test-Path $enrollmentPath) {
        $enrollmentEntries = Get-ChildItem -Path $enrollmentPath -ErrorAction SilentlyContinue |
            Where-Object { $_.PSChildName -match '^[0-9A-Fa-f\-]{36}$' }

        foreach ($entry in $enrollmentEntries) {
            $providerID = (Get-ItemProperty -Path $entry.PSPath -Name "ProviderID" -ErrorAction SilentlyContinue).ProviderID
            if ($providerID) {
                $mdmEnrolled = $true
                Write-Log "Found MDM enrollment: ProviderID=$providerID (EnrollmentID=$($entry.PSChildName))"
                break
            }
        }
    }

    if ($mdmEnrolled) {
        # Double-check: Is enrollment status tracking showing completed?
        $espPath = "HKLM:\SOFTWARE\Microsoft\Windows\Autopilot\EnrollmentStatusTracking\Device\Setup"
        $hasCompleted = (Get-ItemProperty -Path $espPath -Name "HasProvisioningCompleted" -ErrorAction SilentlyContinue).HasProvisioningCompleted

        if ($hasCompleted -eq 1) {
            Write-Log "SKIP: Autopilot enrollment already completed (HasProvisioningCompleted=1). Device is already enrolled."
            exit 0
        }
    }

    # Check 3: Is the agent already installed? (leftover from previous run)
    if (Test-Path $AgentBinPath) {
        $existingAgent = Get-ChildItem -Path $AgentBinPath -Filter "AutopilotMonitor.Agent.exe" -ErrorAction SilentlyContinue
        if ($existingAgent) {
            Write-Log "SKIP: Agent already installed at $($existingAgent.FullName). Previous enrollment may have been in progress. Please check if enrollment completed or clean up C:\ProgramData\AutopilotMonitor before re-running."
            exit 0
        }
    }

    Write-Log "Pre-flight checks passed - device is freshly provisioned and enrollment in progress"

    # Download and extract agent binaries
    $agentExePath = Join-Path $AgentBinPath "AutopilotMonitor.Agent.exe"

    if (Test-Path $agentExePath) {
        Write-Log "Agent already installed at $agentExePath"
    }
    else {
        Write-Log "Downloading agent from $AgentDownloadUrl..."

        try {
            # Download agent ZIP
            $zipPath = Join-Path $env:TEMP "AutopilotMonitor-Agent.zip"
            $maxDownloadAttempts = 3
            $downloadAttempt = 0
            $downloadResponse = $null

            do {
                $downloadAttempt++
                try {
                    Write-Log "Download attempt ${downloadAttempt}/${maxDownloadAttempts}"
                    $downloadResponse = Invoke-WebRequest `
                        -Uri $AgentDownloadUrl `
                        -OutFile $zipPath `
                        -UseBasicParsing `
                        -TimeoutSec 30 `
                        -ErrorAction Stop `
                        -PassThru
                    Write-Log "Downloaded agent to $zipPath"
                    break
                }
                catch {
                    if ($downloadAttempt -ge $maxDownloadAttempts) {
                        throw
                    }

                    $retryDelaysInSeconds = @(2, 4, 8)
                    $retryDelaySeconds = $retryDelaysInSeconds[$downloadAttempt - 1]
                    Write-Log "Download failed (attempt $downloadAttempt): $($_.Exception.Message). Retrying in ${retryDelaySeconds}s..."
                    Start-Sleep -Seconds $retryDelaySeconds
                }
            } while ($downloadAttempt -lt $maxDownloadAttempts)

            # Integrity check: compare HTTP Content-MD5 with local ZIP hash
            $expectedMd5Header = $downloadResponse.Headers["Content-MD5"]
            $expectedMd5 = if ($expectedMd5Header -is [System.Array]) { "$($expectedMd5Header[0])".Trim() } else { "$expectedMd5Header".Trim() }
            if ($expectedMd5 -notmatch '\S') {
                Write-Log "WARNING: Response has no Content-MD5 header - skipping MD5 integrity validation"
            }
            else {
                $actualMd5 = Get-FileMd5Base64 -Path $zipPath
                Write-Log "Validating Content-MD5 header against downloaded ZIP"
                if ($actualMd5 -ne $expectedMd5) {
                    throw "MD5 integrity check failed. Expected (Content-MD5)='$expectedMd5', Actual='$actualMd5'"
                }
                Write-Log "MD5 integrity check passed"
            }

            # Extract to agent bin path
            Expand-Archive -Path $zipPath -DestinationPath $AgentBinPath -Force
            Write-Log "Extracted agent to $AgentBinPath"

            # Cleanup
            Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
            Write-Log "Cleaned up temporary files"

            # Verify extraction
            if (-not (Test-Path $agentExePath)) {
                throw "Agent executable not found after extraction at $agentExePath"
            }

            Write-Log "Agent installation completed successfully"
        }
        catch {
            Write-Log "ERROR downloading/extracting agent: $($_.Exception.Message)"
            throw
        }
    }

    # Let the agent install/deploy itself and manage its own Scheduled Task
    Write-Log "Calling agent install mode (--install)..."
    & $agentExePath --install
    $installExitCode = $LASTEXITCODE
    if ($installExitCode -ne 0) {
        throw "Agent install failed with exit code $installExitCode"
    }
    Write-Log "Agent install mode completed successfully"

    Write-Log "===== Bootstrap Completed Successfully ====="
    Write-Log "Agent Path: $AgentBasePath"
    Write-Log "Scheduled Task: managed by agent (--install)"
    Write-Log "Agent will monitor enrollment and auto-remove when complete"
    Write-Log "Bootstrap log: $LogFile"
    Write-Log ""
    Write-Log "IMPORTANT: Agent runs as Scheduled Task and will:"
    Write-Log "  - Generate unique session ID on startup"
    Write-Log "  - Survive reboots during enrollment"
    Write-Log "  - Monitor enrollment phases in real-time"
    Write-Log "  - Upload events to built-in backend URL (or CLI override /--backend-api)"
    Write-Log "  - Self-destruct when enrollment completes"
    Write-Log "  - Clean removal: delete C:\ProgramData\AutopilotMonitor folder"

    exit 0
}
catch {
    Write-Log "===== Bootstrap FAILED ====="
    Write-Log "ERROR: $($_.Exception.Message)"
    Write-Log "Stack trace: $($_.ScriptStackTrace)"
    Write-Log "Please check log file: $LogFile"
    exit 1
}
