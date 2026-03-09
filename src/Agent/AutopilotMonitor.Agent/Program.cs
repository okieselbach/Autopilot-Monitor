using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AutopilotMonitor.Agent.Core.Configuration;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Agent.Core.Monitoring.Core;
using AutopilotMonitor.Shared;

namespace AutopilotMonitor.Agent
{
    partial class Program
    {
        static void Main(string[] args)
        {
            // Install mode: ensure folders, deploy payload, create/update Scheduled Task, and start it.
            if (args.Contains("--install"))
            {
                RunInstallMode(args);
                return;
            }

            // Gather rules mode: fetch remote config, execute all startup gather rules against the
            // current (or newly created) session, upload the collected events, then exit.
            if (args.Contains("--run-gather-rules"))
            {
                RunGatherRulesMode(args);
                return;
            }

            // Always run directly - no ServiceBase.Run.
            // The agent is started by the Scheduled Task (SYSTEM) or manually with --console.
            // Both paths land here and run identically; --console just enables console output.
            RunAgent(args);
        }

        static void RunAgent(string[] args)
        {
            var consoleMode = args.Contains("--console") || Environment.UserInteractive;

            // Process guard: prevent multiple agent instances from running
            if (IsAnotherAgentInstanceRunning())
            {
                var message = "Another agent process is already running. This instance will exit.";
                if (consoleMode)
                    Console.WriteLine($"ERROR: {message}");

                // Try to log if possible, but this might fail if logger isn't initialized yet
                try
                {
                    var logDir = Environment.ExpandEnvironmentVariables(Constants.LogDirectory);
                    var logger = new AgentLogger(logDir);
                    logger.Warning(message);
                }
                catch { }

                Environment.Exit(1);
                return;
            }

            if (consoleMode)
            {
                Console.WriteLine("Autopilot Monitor Agent");
                Console.WriteLine("=======================");
                Console.WriteLine();
            }

            // Self-update: check for newer agent version in blob storage and apply if available.
            // Priority: speed over update — 1s version check, 10s download timeout.
            // Any failure → continue with current version (never block startup).
            var agentDir = Environment.ExpandEnvironmentVariables(Constants.AgentDirectory);
            SelfUpdater.CleanupPreviousUpdate(agentDir, msg => { if (consoleMode) Console.WriteLine(msg); });
            SelfUpdater.CheckAndApplyUpdateAsync(GetAgentVersion(), agentDir, consoleMode).GetAwaiter().GetResult();
            // If we reach here, no update was applied — continue normal startup

            try
            {
                var config = LoadConfiguration(args);
                var logDir = Environment.ExpandEnvironmentVariables(config.LogDirectory);
                var logger = new AgentLogger(logDir, config.LogLevel);
                var agentVersion = GetAgentVersion();

                logger.Info($"======================= Agent starting ({(consoleMode ? "console" : "background/SYSTEM")}) =======================");
                logger.Info($"Agent version: {agentVersion}");

                // Check for enrollment complete marker (handles scheduled task cleanup retry)
                if (CheckEnrollmentCompleteMarker(config, logger, consoleMode))
                {
                    // Marker was found and handled (cleanup executed or skipped) - exit
                    return;
                }

                // Emergency break: absolute session age check across restarts.
                // Prevents zombie agents that stay on the device forever due to logic errors.
                if (CheckSessionAgeEmergencyBreak(config, logger, consoleMode))
                {
                    return;
                }

                if (consoleMode)
                {
                    Console.WriteLine($"Agent Version: {agentVersion}");
                    Console.WriteLine($"Session ID:    {config.SessionId}");
                    Console.WriteLine($"Tenant ID:     {config.TenantId}");
                    Console.WriteLine($"API URL:       {config.ApiBaseUrl}");
                    Console.WriteLine($"Log Dir:       {logDir}");
                    Console.WriteLine($"Keep Logs:     {config.KeepLogFile}");
                    if (!string.IsNullOrEmpty(config.ReplayLogDir))
                        Console.WriteLine($"Replay Dir:  {config.ReplayLogDir} ({config.ReplaySpeedFactor}x)");
                    if (config.RebootOnComplete) Console.WriteLine("Reboot:      ON COMPLETE");
                    if (config.UseClientCertAuth)
                    {
                        Console.WriteLine($"Cert Auth:   ENABLED");
                        if (!string.IsNullOrEmpty(config.ClientCertThumbprint))
                            Console.WriteLine($"Thumbprint:  {config.ClientCertThumbprint}");
                    }
                    Console.WriteLine();
                }

                using (var service = new MonitoringService(config, logger, agentVersion))
                {
                    service.Start();

                    if (consoleMode)
                    {
                        Console.WriteLine("Agent is running. Press Enter to stop...");
                        Console.ReadLine();
                        service.Stop();
                        Console.WriteLine("Agent stopped.");
                    }
                    else
                    {
                        // Running as Scheduled Task under SYSTEM - block until the monitoring
                        // service signals completion via self-destruct / process exit.
                        service.WaitForCompletion();
                    }
                }

                logger.Info("Agent stopped");
            }
            catch (Exception ex)
            {
                // Last-resort: write to a crash file next to the log directory since we
                // cannot use the Event Log (no traces policy).
                try
                {
                    var crashPath = Path.Combine(
                        Environment.ExpandEnvironmentVariables(Constants.LogDirectory),
                        $"crash_{DateTime.UtcNow:yyyyMMdd_HHmmss}.log");
                    Directory.CreateDirectory(Path.GetDirectoryName(crashPath));
                    File.WriteAllText(crashPath, $"[{DateTime.UtcNow:u}] FATAL: {ex}");
                }
                catch { /* nowhere left to log */ }

                if (consoleMode)
                {
                    Console.WriteLine($"ERROR: {ex.Message}");
                    Console.WriteLine(ex.StackTrace);
                }
            }
        }

        /// <summary>
        /// Checks if another AutopilotMonitor.Agent.exe process is already running.
        /// </summary>
        static bool IsAnotherAgentInstanceRunning()
        {
            try
            {
                var currentProcess = Process.GetCurrentProcess();
                var processes = Process.GetProcessesByName(currentProcess.ProcessName);

                // If there's more than one process with our name, another instance is running
                return processes.Length > 1;
            }
            catch
            {
                // If we can't check, assume no conflict
                return false;
            }
        }
    }

    /// <summary>
    /// Persisted bootstrap configuration for OOBE-bootstrapped agents.
    /// Saved to %ProgramData%\AutopilotMonitor\bootstrap-config.json by RunInstallMode
    /// so the Scheduled Task can pick it up on restart.
    /// </summary>
    class BootstrapConfigFile
    {
        public string BootstrapToken { get; set; }
        public string TenantId { get; set; }
    }
}
