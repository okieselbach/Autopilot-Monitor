<#
.SYNOPSIS
    Bootstrap script to deploy and start the Autopilot Monitor agent.

.DESCRIPTION
    This script is designed to be deployed via Intune as a PowerShell Script during Autopilot.
    It runs VERY EARLY in the enrollment process (first Intune action) and:
    1. Creates a unique session ID for this enrollment
    2. Downloads the monitoring agent binaries
    3. Configures the agent (filesystem only - NO registry)
    4. Registers agent as Scheduled Task (runs on computer startup)
    5. Agent self-destructs when enrollment completes

.PARAMETER ApiBaseUrl
    The base URL of the backend API (default: http://localhost:7071 for local testing)
    Production example: https://autopilot-api.azurewebsites.net

.PARAMETER AgentDownloadUrl
    URL to download the agent binaries from (ZIP file)

.EXAMPLE
    .\Install-AutopilotMonitor.ps1
    (Uses default local API for testing)

.EXAMPLE
    .\Install-AutopilotMonitor.ps1 -ApiBaseUrl "https://autopilot-api.azurewebsites.net"

.NOTES
    - Agent is temporary and auto-removes after enrollment
    - Everything in C:\ProgramData\AutopilotMonitor (easy cleanup)
    - No Registry entries (clean machine after removal)
    - Scheduled Task survives reboots during enrollment
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    #[string]$ApiBaseUrl = "http://localhost:7071",
    [string]$ApiBaseUrl = "https://autopilotmonitor-func.azurewebsites.net",

    [Parameter(Mandatory = $false)]
    [string]$AgentDownloadUrl = "https://autopilotmonitor.blob.core.windows.net/agent/AutopilotMonitor-Agent.zip",
    
    [Parameter(Mandatory = $false)]
    [int]$MaxOsAgeHours = 5
)

# Configuration - Everything in ProgramData for easy cleanup
$AgentBasePath = "$env:ProgramData\AutopilotMonitor"
$AgentBinPath = "$AgentBasePath\Agent"
$AgentConfigPath = "$AgentBasePath\Config"
$AgentRulesPath = "$AgentBasePath\Rules"
$AgentSpoolPath = "$AgentBasePath\Spool"
$AgentLogsPath = "$AgentBasePath\Logs"
$TaskName = "AutopilotMonitor-Agent"
$LogFile = "$AgentLogsPath\Bootstrap.log"

# Ensure directories exist
New-Item -Path $AgentBasePath -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
New-Item -Path $AgentBinPath -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
New-Item -Path $AgentConfigPath -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
New-Item -Path $AgentRulesPath -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
New-Item -Path $AgentSpoolPath -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
New-Item -Path $AgentLogsPath -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null

function Write-Log {
    param([string]$Message)
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $logMessage = "[$timestamp] $Message"
    Write-Output $logMessage
    Add-Content -Path $LogFile -Value $logMessage -ErrorAction SilentlyContinue
}

try {
    Write-Log "===== Autopilot Monitor Bootstrap Started ====="
    Write-Log "API Base URL: $ApiBaseUrl"

    # ── Pre-flight: Skip installation on already-enrolled devices ──

    # Check 1: OS install date must be within threshold (fresh device)
    $osInstallDate = (Get-CimInstance Win32_OperatingSystem).InstallDate
    $osAge = (Get-Date) - $osInstallDate
    Write-Log "OS install date: $osInstallDate (age: $([math]::Round($osAge.TotalHours, 1))h)"

    if ($osAge.TotalHours -gt $MaxOsAgeHours) {
        Write-Log "SKIP: OS was installed $([math]::Round($osAge.TotalHours, 1))h ago (threshold: ${MaxOsAgeHours}h). Device is not freshly provisioned."
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

    Write-Log "Pre-flight checks passed - device is freshly provisioned and enrollment in progress"

    # Create agent configuration (JSON - NO REGISTRY!)
    Write-Log "Creating agent configuration..."
    $agentConfig = @{
        apiBaseUrl = $ApiBaseUrl
        uploadIntervalSeconds = 30
        cleanupOnExit = $true
        selfDestructOnComplete = $true
    }

    $agentConfigJson = $agentConfig | ConvertTo-Json -Depth 10
    $agentConfigFile = Join-Path $AgentConfigPath "agent-config.json"
    Set-Content -Path $agentConfigFile -Value $agentConfigJson -Force
    Write-Log "Configuration saved to $agentConfigFile"

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
            Invoke-WebRequest -Uri $AgentDownloadUrl -OutFile $zipPath -UseBasicParsing -TimeoutSec 120
            Write-Log "Downloaded agent to $zipPath"

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

    # Register as Scheduled Task (survives reboots, easy to remove)
    Write-Log "Registering Scheduled Task..."

    # Check if task already exists
    $existingTask = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue

    if ($null -ne $existingTask) {
        Write-Log "Scheduled Task already exists - removing old task"
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    }

    # Create action - run agent executable
    $action = New-ScheduledTaskAction -Execute $agentExePath -WorkingDirectory $AgentBinPath

    # Create trigger - run at computer startup
    $trigger = New-ScheduledTaskTrigger -AtStartup

    # Create settings - run whether user is logged on or not
    $settings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries `
        -StartWhenAvailable `
        -RunOnlyIfNetworkAvailable `
        -ExecutionTimeLimit (New-TimeSpan -Hours 0)  # No time limit

    # Create principal - run as SYSTEM
    $principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest

    # Register the task
    Register-ScheduledTask `
        -TaskName $TaskName `
        -Action $action `
        -Trigger $trigger `
        -Settings $settings `
        -Principal $principal `
        -Description "Autopilot Monitor Agent - Temporary enrollment monitoring" `
        -Force

    Write-Log "Scheduled Task registered successfully"

    # Start the task immediately (don't wait for next reboot)
    Write-Log "Starting agent task..."
    Start-ScheduledTask -TaskName $TaskName
    Write-Log "Agent task started"

    Write-Log "===== Bootstrap Completed Successfully ====="
    Write-Log "Agent Path: $AgentBasePath"
    Write-Log "Scheduled Task: $TaskName"
    Write-Log "API Base URL: $ApiBaseUrl"
    Write-Log "Agent will monitor enrollment and auto-remove when complete"
    Write-Log "Bootstrap log: $LogFile"
    Write-Log ""
    Write-Log "IMPORTANT: Agent runs as Scheduled Task and will:"
    Write-Log "  - Generate unique session ID on startup"
    Write-Log "  - Survive reboots during enrollment"
    Write-Log "  - Monitor enrollment phases in real-time"
    Write-Log "  - Upload events to $ApiBaseUrl"
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
