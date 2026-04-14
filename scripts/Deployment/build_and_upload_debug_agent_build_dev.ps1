# Upload DEV Debug Agent Build Script
# Builds the Debug output, creates a separate -dev ZIP archive, and uploads it
# (plus version-dev.json and the DEV bootstrapper) to Azure Blob Storage.
#
# Unterschiede zum produktiven Script (build_and_upload_debug_agent_build.ps1):
#   - ZIP-Name:            AutopilotMonitor-Agent-dev.zip  (nicht AutopilotMonitor-Agent.zip)
#   - Integrity-File:      version-dev.json                (nicht version.json)
#   - Bootstrap-Upload:    Install-AutopilotMonitor-Dev.ps1 (nicht Install-AutopilotMonitor.ps1)
#   - KEIN Upload von:     version.json, Install-AutopilotMonitor.ps1, Test-ShouldBootstrapAgent.ps1
#   - KEIN Update von:     AdminConfiguration Table (Hash-Oracle bleibt produktiv unangetastet)
#
# => Produktion wird bit-identisch NICHT beeinflusst. Lab-Geraete ziehen via separater
#    Intune-Assignment das Dev-ZIP ueber den Dev-Bootstrapper.

$ErrorActionPreference = "Stop"

# Configuration
$projectPath = "C:\Code\GitHubRepos\Autopilot-Monitor\src\Agent\AutopilotMonitor.Agent\AutopilotMonitor.Agent.csproj"
$summaryDialogProjectPath = "C:\Code\GitHubRepos\Autopilot-Monitor\src\Agent\AutopilotMonitor.SummaryDialog\AutopilotMonitor.SummaryDialog.csproj"
$buildConfiguration = "Debug"
$sourcePath = "C:\Code\GitHubRepos\Autopilot-Monitor\src\Agent\AutopilotMonitor.Agent\bin\Debug\net48"
$summaryDialogOutputPath = "C:\Code\GitHubRepos\Autopilot-Monitor\src\Agent\AutopilotMonitor.SummaryDialog\bin\Debug\net48"
$bootstrapScriptPath = "C:\Code\GitHubRepos\Autopilot-Monitor\scripts\Bootstrap\Install-AutopilotMonitor-Dev.ps1"
$zipFileName = "AutopilotMonitor-Agent-dev.zip"
$versionJsonDevName = "version-dev.json"
$tempZipPath = [System.IO.Path]::Combine($env:TEMP, $zipFileName)
$tempVersionDevPath = [System.IO.Path]::Combine($env:TEMP, $versionJsonDevName)
$bootstrapScriptName = [System.IO.Path]::GetFileName($bootstrapScriptPath)
$sasUrl = "https://autopilotmonitor.blob.core.windows.net/agent?sp=cw&st=2026-02-17T16:10:55Z&se=2028-02-18T00:25:55Z&spr=https&sv=2024-11-04&sr=c&sig=kA%2BeZlpqf4fYUvmO5YgFv%2Fk7yr6oQLPA51%2FDSQEl7hs%3D"
$sasQuery = $sasUrl.Substring($sasUrl.IndexOf('?'))
$blobUrl = "https://autopilotmonitor.blob.core.windows.net/agent/$zipFileName$sasQuery"
$bootstrapBlobUrl = "https://autopilotmonitor.blob.core.windows.net/agent/$bootstrapScriptName$sasQuery"
$versionDevBlobUrl = "https://autopilotmonitor.blob.core.windows.net/agent/$versionJsonDevName$sasQuery"

Write-Host "=== DEV Agent Build and Upload ===" -ForegroundColor Magenta
Write-Host "Project: $projectPath" -ForegroundColor Gray
Write-Host "Build output folder: $sourcePath" -ForegroundColor Gray
Write-Host "Dev bootstrap script: $bootstrapScriptPath" -ForegroundColor Gray
Write-Host "Dev ZIP name: $zipFileName" -ForegroundColor Gray
Write-Host "Dev integrity file: $versionJsonDevName" -ForegroundColor Gray
Write-Host "Started at (UTC): $([DateTime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ss'))" -ForegroundColor Gray

try {
    # [1] Build Agent
    Write-Host "`n[1/7] Building Agent project ($buildConfiguration)..." -ForegroundColor Yellow
    dotnet build $projectPath -c $buildConfiguration
    if ($LASTEXITCODE -ne 0) {
        throw "Agent build failed with exit code $LASTEXITCODE"
    }
    Write-Host "  Agent build completed successfully" -ForegroundColor Green

    # Verify EXE version matches version.json (prevents silent MSBuild version timing bugs)
    $versionJsonCheck = Get-Content (Join-Path $sourcePath "version.json") -Raw | ConvertFrom-Json
    $exeVersionCheck = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $sourcePath "AutopilotMonitor.Agent.exe")).ProductVersion
    $exeVersionClean = $exeVersionCheck -replace '\+.*$', ''
    if ($exeVersionClean -ne $versionJsonCheck.version) {
        throw "VERSION MISMATCH: EXE reports '$exeVersionClean' but version.json says '$($versionJsonCheck.version)'. MSBuild property evaluation timing issue detected."
    }
    Write-Host "  Version verified: EXE=$exeVersionClean matches version.json=$($versionJsonCheck.version)" -ForegroundColor Green

    # [2] Build SummaryDialog
    Write-Host "`n[2/7] Building SummaryDialog project ($buildConfiguration)..." -ForegroundColor Yellow
    dotnet build $summaryDialogProjectPath -c $buildConfiguration
    if ($LASTEXITCODE -ne 0) {
        throw "SummaryDialog build failed with exit code $LASTEXITCODE"
    }
    Write-Host "  SummaryDialog build completed successfully" -ForegroundColor Green

    # [3] Run Agent unit tests
    Write-Host "`n[3/7] Running Agent unit tests..." -ForegroundColor Yellow
    $testProjectPath = "C:\Code\GitHubRepos\Autopilot-Monitor\src\Agent\AutopilotMonitor.Agent.Core.Tests\AutopilotMonitor.Agent.Core.Tests.csproj"
    dotnet test $testProjectPath -c $buildConfiguration --nologo -v quiet
    if ($LASTEXITCODE -ne 0) {
        throw "Agent unit tests failed with exit code $LASTEXITCODE"
    }
    Write-Host "  All agent tests passed" -ForegroundColor Green

    # Copy SummaryDialog EXE + config into Agent output folder
    $summaryDialogExe = Join-Path $summaryDialogOutputPath "AutopilotMonitor.SummaryDialog.exe"
    $summaryDialogConfig = Join-Path $summaryDialogOutputPath "AutopilotMonitor.SummaryDialog.exe.config"
    if (Test-Path $summaryDialogExe) {
        Copy-Item $summaryDialogExe -Destination $sourcePath -Force
        if (Test-Path $summaryDialogConfig) {
            Copy-Item $summaryDialogConfig -Destination $sourcePath -Force
        }
        Write-Host "  Copied SummaryDialog.exe + .config into Agent output" -ForegroundColor Green
    } else {
        Write-Host "  WARNING: SummaryDialog.exe not found at $summaryDialogExe" -ForegroundColor Yellow
    }

    if (-not (Test-Path $sourcePath)) {
        throw "Build output folder not found: $sourcePath"
    }
    if (-not (Test-Path $bootstrapScriptPath)) {
        throw "Dev bootstrap script not found: $bootstrapScriptPath"
    }

    # [4] Create ZIP archive (dev name)
    Write-Host "`n[4/7] Creating DEV ZIP archive..." -ForegroundColor Yellow
    if (Test-Path $tempZipPath) {
        Remove-Item $tempZipPath -Force
        Write-Host "  Existing temporary ZIP removed" -ForegroundColor Gray
    }

    Compress-Archive -Path "$sourcePath\*" -DestinationPath $tempZipPath -CompressionLevel Optimal
    $zipSize = (Get-Item $tempZipPath).Length / 1MB
    Write-Host "  ZIP created: $([math]::Round($zipSize, 2)) MB" -ForegroundColor Green

    $sha256Hash = (Get-FileHash $tempZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "  ZIP SHA-256: $sha256Hash" -ForegroundColor Gray

    $exeSha256Hash = (Get-FileHash (Join-Path $sourcePath "AutopilotMonitor.Agent.exe") -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "  EXE SHA-256: $exeSha256Hash" -ForegroundColor Gray

    # Build version-dev.json IN TEMP. Prod version.json im bin-Ordner bleibt unberuehrt,
    # damit ein versehentlicher Prod-Script-Lauf spaeter nicht auf einem verseuchten
    # Zustand aufsetzt.
    $versionDevObj = [PSCustomObject]@{
        version = $versionJsonCheck.version
        sha256  = $sha256Hash
    }

    # Parse bootstrap script version from DEV bootstrapper
    $bootstrapVersion = $null
    $bootstrapContent = Get-Content $bootstrapScriptPath -Raw
    if ($bootstrapContent -match '\$ScriptVersion\s*=\s*"([\d\.\-a-zA-Z]+)"') {
        $bootstrapVersion = $Matches[1]
        $versionDevObj | Add-Member -NotePropertyName "bootstrapVersion" -NotePropertyValue $bootstrapVersion -Force
        Write-Host "  Dev bootstrap script version: $bootstrapVersion" -ForegroundColor Gray
    } else {
        Write-Host "  WARNING: Could not parse bootstrap `$ScriptVersion from $bootstrapScriptPath" -ForegroundColor Yellow
    }

    $versionDevObj | ConvertTo-Json -Compress | Set-Content $tempVersionDevPath -Encoding UTF8
    Write-Host "  version-dev.json created: $(Get-Content $tempVersionDevPath -Raw)" -ForegroundColor Gray

    $headers = @{ "x-ms-blob-type" = "BlockBlob" }

    # [5] Upload DEV ZIP
    Write-Host "`n[5/7] Uploading DEV agent ZIP to Azure Blob Storage..." -ForegroundColor Yellow
    $fileBytes = [System.IO.File]::ReadAllBytes($tempZipPath)
    Invoke-RestMethod -Uri $blobUrl `
                      -Method Put `
                      -Headers $headers `
                      -Body $fileBytes `
                      -ContentType "application/zip"
    Write-Host "  DEV agent ZIP upload completed successfully" -ForegroundColor Green

    # [6] Upload DEV bootstrap script
    Write-Host "`n[6/7] Uploading DEV bootstrap script to Azure Blob Storage..." -ForegroundColor Yellow
    $scriptBytes = [System.IO.File]::ReadAllBytes($bootstrapScriptPath)
    Invoke-RestMethod -Uri $bootstrapBlobUrl `
                      -Method Put `
                      -Headers $headers `
                      -Body $scriptBytes `
                      -ContentType "text/plain; charset=utf-8"
    Write-Host "  DEV bootstrap script upload completed successfully" -ForegroundColor Green

    # [7] Upload version-dev.json
    Write-Host "`n[7/7] Uploading version-dev.json to Azure Blob Storage..." -ForegroundColor Yellow
    $versionDevBytes = [System.IO.File]::ReadAllBytes($tempVersionDevPath)
    Invoke-RestMethod -Uri $versionDevBlobUrl `
                      -Method Put `
                      -Headers $headers `
                      -Body $versionDevBytes `
                      -ContentType "application/json"
    Write-Host "  version-dev.json upload completed successfully" -ForegroundColor Green

    # NOTE: Kein Upload von version.json / Install-AutopilotMonitor.ps1 / Test-ShouldBootstrapAgent.ps1
    # NOTE: Kein AdminConfiguration Table-Update (Prod-Hash-Oracle bleibt unangetastet)

    # Cleanup
    Remove-Item $tempZipPath -Force -ErrorAction SilentlyContinue
    Remove-Item $tempVersionDevPath -Force -ErrorAction SilentlyContinue

    Write-Host "`n=== DEV build and upload completed successfully ===" -ForegroundColor Green
    Write-Host "Files uploaded: $zipFileName, $bootstrapScriptName, $versionJsonDevName" -ForegroundColor Gray
    Write-Host "Container: autopilotmonitor/agent" -ForegroundColor Gray
    Write-Host "Agent version: $($versionJsonCheck.version) | Bootstrap version: $bootstrapVersion" -ForegroundColor Gray
    Write-Host "Production artifacts (AutopilotMonitor-Agent.zip / version.json / AdminConfiguration) were NOT touched." -ForegroundColor Gray

} catch {
    Write-Host "`nERROR: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Details: $($_.Exception.ToString())" -ForegroundColor Red
    exit 1
}
