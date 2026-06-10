using System;
using System.Diagnostics;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Security;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime
{
    /// <summary>
    /// Handles agent cleanup on enrollment completion:
    /// removes the Scheduled Task and deletes all agent files (self-destruct).
    /// </summary>
    public class CleanupService
    {
        private readonly AgentConfiguration _configuration;
        private readonly AgentLogger _logger;

        public CleanupService(AgentConfiguration configuration, AgentLogger logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>
        /// Executes full self-destruct: removes scheduled task and deletes all files
        /// </summary>
        public virtual void ExecuteSelfDestruct()
        {
            try
            {
                _logger.Info($"Executing FULL SELF-DESTRUCT (Scheduled Task + Files{(_configuration.RebootOnComplete ? " + Reboot" : "")})");

                var currentProcessId = Process.GetCurrentProcess().Id;
                var agentBasePath = Path.GetFullPath(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "AutopilotMonitor"
                ));

                var logDir = Path.GetFullPath(Path.Combine(agentBasePath, "Logs"));
                var keepLogs = _configuration.KeepLogFile;

                // Create a self-deleting PowerShell script
                var cleanupScript = $@"
$scriptPath = $MyInvocation.MyCommand.Path

# Wait for agent process to exit
Start-Sleep -Seconds 2
Wait-Process -Id {currentProcessId} -ErrorAction SilentlyContinue -Timeout 30
Start-Sleep -Seconds 1

# Remove Scheduled Task — verify removal BEFORE deleting files (LIFE-F6). An orphan task firing
# against a deleted exe spams Task Scheduler errors every boot forever and, once the files (incl.
# the enrollment-complete marker) are gone, has no cleanup-retry path. So confirm the task is gone
# first; if it cannot be removed, SKIP the file deletion and leave everything intact for a clean
# cleanup retry on the next boot (the marker survives, so bootstrap re-runs cleanup).
$taskName = '{_configuration.ScheduledTaskName}'
$taskGone = $false
for ($i = 1; $i -le 5; $i++) {{
    try {{ Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue }} catch {{ }}
    try {{ Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue }} catch {{ }}
    $still = $null
    try {{ $still = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue }} catch {{ }}
    if (-not $still) {{ $taskGone = $true; break }}
    Start-Sleep -Seconds 1
}}
if (-not $taskGone) {{
    # Fallback: schtasks.exe /Delete does not depend on the ScheduledTasks PowerShell module.
    try {{ & schtasks.exe /Delete /TN $taskName /F 2>$null | Out-Null }} catch {{ }}
    try {{ $taskGone = -not (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) }} catch {{ }}
}}

{(keepLogs ? $@"
if ($taskGone) {{
    # Delete everything except Logs directory
    Get-ChildItem -Path '{agentBasePath}' -Exclude 'Logs' | ForEach-Object {{
        $dest = $_.FullName + '.del'
        try {{ Rename-Item -Path $_.FullName -NewName $dest -Force -ErrorAction SilentlyContinue }} catch {{ }}
        Remove-Item -Path $dest -Recurse -Force -ErrorAction SilentlyContinue
    }}
}}
" : $@"
if ($taskGone) {{
    # Rename folder then delete. Retry a few times to let the OS release handles.
    $renamedPath = '{agentBasePath}.del'
    $renamed = $false
    for ($i = 1; $i -le 10; $i++) {{
        try {{
            Rename-Item -Path '{agentBasePath}' -NewName $renamedPath -Force -ErrorAction Stop
            $renamed = $true
            break
        }} catch {{
            Start-Sleep -Seconds 2
        }}
    }}
    if ($renamed) {{
        Remove-Item -Path $renamedPath -Recurse -Force -ErrorAction SilentlyContinue
    }} else {{
        Remove-Item -Path '{agentBasePath}' -Recurse -Force -ErrorAction SilentlyContinue
    }}
}}
")}
{(_configuration.RebootOnComplete ? @"
# shutdown.exe (NOT Restart-Computer): Restart-Computer has no -Comment parameter and -Delay is
# only honoured with -Wait, so the previous invocation failed parameter binding and the device
# silently never rebooted. shutdown.exe /r /t 10 mirrors the standalone reboot path and reboots
# after a 10 s delay (lets this script finish + the agent exit). Review LIFE-F2.
shutdown.exe /r /t 10 /c 'Autopilot enrollment completed - Autopilot Monitor is rebooting'
" : "")}
Remove-Item -Path $scriptPath -Force -ErrorAction SilentlyContinue
";

                // Write cleanup script to temp location (outside of agent folder)
                var tempScriptPath = Path.Combine(Path.GetTempPath(), $"AutopilotMonitor-Cleanup-{Guid.NewGuid()}.ps1");
                File.WriteAllText(tempScriptPath, cleanupScript);
                _logger.Info($"Cleanup script written to {tempScriptPath}");

                // Change CWD to temp so this process no longer holds a reference into
                // the AutopilotMonitor folder tree - Windows won't allow renaming a
                // directory that any process has as its current working directory.
                try { Directory.SetCurrentDirectory(Path.GetTempPath()); } catch { }

                // Launch via cmd /c start so the powershell process is created outside the
                // current Job Object (Scheduled Task job). cmd's 'start' command always
                // creates a new process group that breaks job inheritance, even under SYSTEM.
                var psi = new ProcessStartInfo
                {
                    FileName = SystemPaths.Cmd,
                    // Absolute path to powershell.exe is passed to `start` to prevent PATH hijacking
                    // (the `start` builtin does its own PATH resolution for the target command).
                    Arguments = $"/c start \"\" /b \"{SystemPaths.PowerShell}\" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{tempScriptPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetTempPath()
                };

                Process.Start(psi);
                _logger.Info("Cleanup script launched. Agent will now exit.");
            }
            catch (Exception ex)
            {
                _logger.Error("Error executing self-destruct", ex);
            }
        }

    }
}
