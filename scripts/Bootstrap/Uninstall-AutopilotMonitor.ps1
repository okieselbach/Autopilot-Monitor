<#
.SYNOPSIS
    Cleanup script to manually remove the Autopilot Monitor agent.

.DESCRIPTION
    This script removes all traces of the Autopilot Monitor agent:
    - Stops and removes the Scheduled Task
    - Deletes C:\ProgramData\AutopilotMonitor folder

    Normally the agent self-destructs after enrollment completion.
    Use this script only for manual cleanup or troubleshooting.

.EXAMPLE
    .\Uninstall-AutopilotMonitor.ps1

.NOTES
    - Requires Administrator privileges
    - Safe to run multiple times
    - No Registry cleanup needed (agent doesn't use Registry)
#>

[CmdletBinding()]
param()

$TaskName = "AutopilotMonitor-Agent"
$AgentBasePath = "$env:ProgramData\AutopilotMonitor"

Write-Host "===== Autopilot Monitor Cleanup Started =====" -ForegroundColor Cyan

# Stop and remove Scheduled Task
try {
    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue

    if ($null -ne $task) {
        Write-Host "Stopping Scheduled Task..." -ForegroundColor Yellow
        Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue

        Write-Host "Removing Scheduled Task..." -ForegroundColor Yellow
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction Stop
        Write-Host "  ✓ Scheduled Task removed" -ForegroundColor Green
    }
    else {
        Write-Host "  → Scheduled Task not found (already removed)" -ForegroundColor Gray
    }
}
catch {
    Write-Host "  ✗ Error removing Scheduled Task: $($_.Exception.Message)" -ForegroundColor Red
}

# Delete agent folder
try {
    if (Test-Path $AgentBasePath) {
        Write-Host "Deleting agent folder: $AgentBasePath" -ForegroundColor Yellow

        # Wait a moment to ensure agent has stopped
        Start-Sleep -Seconds 2

        Remove-Item -Path $AgentBasePath -Recurse -Force -ErrorAction Stop
        Write-Host "  ✓ Agent folder deleted" -ForegroundColor Green
    }
    else {
        Write-Host "  → Agent folder not found (already removed)" -ForegroundColor Gray
    }
}
catch {
    Write-Host "  ✗ Error deleting folder: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "  → You may need to manually delete: $AgentBasePath" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "===== Cleanup Completed =====" -ForegroundColor Cyan
Write-Host "Autopilot Monitor agent has been removed from this device." -ForegroundColor Green
Write-Host ""
