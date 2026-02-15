using System;
using System.Diagnostics;
using System.IO;
using AutopilotMonitor.Agent.Core.Configuration;
using AutopilotMonitor.Agent.Core.Logging;

namespace AutopilotMonitor.Agent.Core.Monitoring.Core
{
    /// <summary>
    /// Handles agent self-destruct and cleanup operations (PowerShell script generation and launch).
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
        public void ExecuteSelfDestruct()
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

# Remove Scheduled Task
try {{
    Stop-ScheduledTask -TaskName '{_configuration.ScheduledTaskName}' -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName '{_configuration.ScheduledTaskName}' -Confirm:$false -ErrorAction SilentlyContinue
}} catch {{ }}

{(keepLogs ? $@"
# Delete everything except Logs directory
Get-ChildItem -Path '{agentBasePath}' -Exclude 'Logs' | ForEach-Object {{
    $dest = $_.FullName + '.del'
    try {{ Rename-Item -Path $_.FullName -NewName $dest -Force -ErrorAction SilentlyContinue }} catch {{ }}
    Remove-Item -Path $dest -Recurse -Force -ErrorAction SilentlyContinue
}}
" : $@"
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
")}
{(_configuration.RebootOnComplete ? @"
Restart-Computer -Force -Delay 10 -Comment 'Autopilot enrollment completed - Autopilot Monitor is rebooting'
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
                    FileName = "cmd.exe",
                    Arguments = $"/c start \"\" /b powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{tempScriptPath}\"",
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

        /// <summary>
        /// Executes cleanup only (files) without removing scheduled task
        /// </summary>
        public void ExecuteCleanup()
        {
            try
            {
                _logger.Info($"Executing CLEANUP (Files only, keeping Scheduled Task{(_configuration.RebootOnComplete ? " + Reboot" : "")})");

                var currentProcessId = Process.GetCurrentProcess().Id;
                var agentBasePath = Path.GetFullPath(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "AutopilotMonitor"
                ));

                var logDir = Path.GetFullPath(Path.Combine(agentBasePath, "Logs"));
                var keepLogs = _configuration.KeepLogFile;

                // Create a self-deleting cleanup PowerShell script
                var cleanupScript = $@"
$scriptPath = $MyInvocation.MyCommand.Path

# Wait for agent process to exit. Wait-Process may fail if the process is already gone - that is fine.
Start-Sleep -Seconds 2
Wait-Process -Id {currentProcessId} -ErrorAction SilentlyContinue -Timeout 30
Start-Sleep -Seconds 1

{(keepLogs ? $@"
# Delete everything except Logs directory.
# Rename subdirs/files first so locked EXE bytes are released, then remove.
Get-ChildItem -Path '{agentBasePath}' -Exclude 'Logs' | ForEach-Object {{
    $dest = $_.FullName + '.del'
    try {{ Rename-Item -Path $_.FullName -NewName $dest -Force -ErrorAction SilentlyContinue }} catch {{ }}
    Remove-Item -Path $dest -Recurse -Force -ErrorAction SilentlyContinue
}}
" : $@"
# Rename the folder first (works even while EXE bytes are still mapped),
# then delete the renamed copy - by then all handles are closed.
$renamedPath = '{agentBasePath}.del'
try {{ Rename-Item -Path '{agentBasePath}' -NewName $renamedPath -Force -ErrorAction Stop }} catch {{ $renamedPath = '{agentBasePath}' }}
Remove-Item -Path $renamedPath -Recurse -Force -ErrorAction SilentlyContinue
")}
{(_configuration.RebootOnComplete ? @"
Restart-Computer -Force
" : "")}
# Delete this script
Remove-Item -Path $scriptPath -Force -ErrorAction SilentlyContinue
";

                // Write cleanup script to temp location
                var tempScriptPath = Path.Combine(Path.GetTempPath(), $"AutopilotMonitor-Cleanup-{Guid.NewGuid()}.ps1");
                File.WriteAllText(tempScriptPath, cleanupScript);
                _logger.Info($"Cleanup script written to {tempScriptPath}");

                // Launch via cmd /c start so the powershell process is created outside the
                // current Job Object (Scheduled Task job). cmd's 'start' command always
                // creates a new process group that breaks job inheritance, even under SYSTEM.
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c start \"\" /b powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{tempScriptPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                Process.Start(psi);
                _logger.Info("Cleanup script launched. Agent will now exit.");
            }
            catch (Exception ex)
            {
                _logger.Error("Error executing cleanup", ex);
            }
        }
    }
}
