using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AutopilotMonitor.Agent.Core.Configuration;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Agent.Core.Monitoring.Core;
using AutopilotMonitor.Agent.Core.Security;
using AutopilotMonitor.Shared;

namespace AutopilotMonitor.Agent
{
    partial class Program
    {
        static void Main(string[] args)
        {
            if (args.Contains("--help") || args.Contains("-h") || args.Contains("-?"))
            {
                PrintUsage();
                return;
            }

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

            // IME matching mode: parse IME log files offline and produce ime_pattern_matching.log.
            if (args.Contains("--run-ime-matching"))
            {
                RunImeMatchingMode(args);
                return;
            }

            // Register ProcessExit handler BEFORE RunAgent so we can distinguish
            // OS shutdown/reboot (clean exit) from crashes (no marker written).
            var stateDir = Environment.ExpandEnvironmentVariables(Constants.AgentDataDirectory);
            var cleanExitMarkerPath = Path.Combine(stateDir, "clean-exit.marker");
            AppDomain.CurrentDomain.ProcessExit += (s, e) =>
            {
                try { File.WriteAllText(cleanExitMarkerPath, DateTime.UtcNow.ToString("O")); }
                catch { }
            };

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

            // Log init banner to separate this process from install-mode logs in the same file
            SelfUpdater.LogInit(GetAgentVersion());

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

                // CLI --log-level override (takes precedence over config file)
                var cliLogLevel = GetArgValue(args, "--log-level");
                if (!string.IsNullOrEmpty(cliLogLevel) && Enum.TryParse<AgentLogLevel>(cliLogLevel, ignoreCase: true, out var parsedLevel))
                    config.LogLevel = parsedLevel;

                var logDir = Environment.ExpandEnvironmentVariables(config.LogDirectory);
                var logger = new AgentLogger(logDir, config.LogLevel);
                if (consoleMode)
                    logger.EnableConsoleOutput = true;

                var agentVersion = GetAgentVersion();

                logger.Info($"======================= Agent starting ({(consoleMode ? "console" : "background/SYSTEM")}) =======================");
                logger.Info($"Agent version: {agentVersion}");
                logger.Info($"Command line: {FormatArgsForLog(args)}");

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
                    if (config.AwaitEnrollment)
                        Console.WriteLine($"Await Enroll: ENABLED (timeout: {config.AwaitEnrollmentTimeoutMinutes}min)");
                    Console.WriteLine();
                }

                // Await-enrollment mode: wait for MDM certificate before proceeding.
                // Used when the agent is deployed before Intune enrollment completes.
                if (config.AwaitEnrollment)
                {
                    logger.Info("Await-enrollment mode active — waiting for MDM certificate before starting");
                    if (consoleMode)
                        Console.WriteLine("Await-enrollment: Waiting for MDM certificate...");

                    var cert = EnrollmentAwaiter.WaitForMdmCertificateAsync(
                        config.ClientCertThumbprint,
                        config.AwaitEnrollmentTimeoutMinutes,
                        logger).GetAwaiter().GetResult();

                    if (cert == null)
                    {
                        logger.Warning("Await-enrollment: No MDM certificate found within timeout. Agent will exit.");
                        if (consoleMode)
                            Console.WriteLine("ERROR: No MDM certificate found within timeout. Agent will exit.");
                        return;
                    }

                    if (consoleMode)
                        Console.WriteLine($"MDM certificate found: {cert.Thumbprint}");

                    // Re-read TenantId from registry — enrollment creates the registry key
                    // alongside the certificate, so it should be available now.
                    if (string.IsNullOrEmpty(config.TenantId))
                    {
                        config.TenantId = GetTenantIdFromRegistry();
                        if (!string.IsNullOrEmpty(config.TenantId))
                        {
                            logger.Info($"Await-enrollment: TenantId discovered from registry: {config.TenantId}");
                            if (consoleMode)
                                Console.WriteLine($"Tenant ID:   {config.TenantId}");
                        }
                        else
                        {
                            logger.Warning("Await-enrollment: MDM certificate found but TenantId not yet in registry");
                        }
                    }

                    // Remove persisted config so subsequent restarts proceed normally
                    try
                    {
                        var awaitConfigPath = Path.Combine(
                            Environment.ExpandEnvironmentVariables(Constants.AgentDataDirectory),
                            "await-enrollment.json");
                        if (File.Exists(awaitConfigPath))
                        {
                            File.Delete(awaitConfigPath);
                            logger.Info("Await-enrollment: Config file removed — subsequent starts will proceed normally");
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Warning($"Await-enrollment: Could not remove config file: {ex.Message}");
                    }
                }

                // Evaluate previous exit signals before starting the monitoring service.
                // clean-exit.marker present  → previous run exited cleanly (OS shutdown, reboot, normal exit)
                // crash_*.log present        → unhandled exception crash (with stack trace)
                // neither                    → hard kill (power loss, BSOD, taskkill /F) or first run
                var cleanExitMarker = Path.Combine(
                    Environment.ExpandEnvironmentVariables(Constants.AgentDataDirectory),
                    "clean-exit.marker");
                var crashLogDir = Environment.ExpandEnvironmentVariables(Constants.LogDirectory);
                var hadCleanExit = File.Exists(cleanExitMarker);
                var crashLogs = Directory.Exists(crashLogDir)
                    ? Directory.GetFiles(crashLogDir, "crash_*.log")
                    : Array.Empty<string>();

                string previousExitType;
                string previousCrashException = null;
                if (hadCleanExit)
                {
                    previousExitType = "clean";
                }
                else if (crashLogs.Length > 0)
                {
                    previousExitType = "exception_crash";
                    // Extract exception type from most recent crash log
                    try
                    {
                        var mostRecent = crashLogs[crashLogs.Length - 1]; // sorted by name = chronological
                        var crashContent = File.ReadAllText(mostRecent);
                        // Format: "[timestamp] FATAL: ExceptionType: message..."
                        var fatalIdx = crashContent.IndexOf("FATAL: ");
                        if (fatalIdx >= 0)
                        {
                            var afterFatal = crashContent.Substring(fatalIdx + 7);
                            var colonIdx = afterFatal.IndexOf(':');
                            if (colonIdx > 0)
                                previousCrashException = afterFatal.Substring(0, colonIdx).Trim();
                        }
                    }
                    catch { }
                }
                else
                {
                    // No marker and no crash log — either first run ever or hard kill
                    var sessionFile = Path.Combine(
                        Environment.ExpandEnvironmentVariables(Constants.AgentDataDirectory), "session.id");
                    previousExitType = File.Exists(sessionFile) ? "hard_kill" : "first_run";
                }

                // Clean up: delete marker and crash logs so next cycle starts fresh
                try { File.Delete(cleanExitMarker); } catch { }
                foreach (var crashLog in crashLogs)
                    try { File.Delete(crashLog); } catch { }

                if (previousExitType != "first_run")
                    logger.Info($"Previous exit: {previousExitType}{(previousCrashException != null ? $" ({previousCrashException})" : "")}");

                using (var service = new MonitoringService(config, logger, agentVersion, previousExitType, previousCrashException))
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

        static void PrintUsage()
        {
            var version = GetAgentVersion();
            Console.WriteLine($"Autopilot Monitor Agent v{version}");
            Console.WriteLine();
            Console.WriteLine("Usage: AutopilotMonitor.Agent.exe [options]");
            Console.WriteLine();
            Console.WriteLine("Modes:");
            Console.WriteLine("  --install                         Deploy payload, create Scheduled Task, and start it");
            Console.WriteLine("  --run-gather-rules                Execute startup gather rules once and exit");
            Console.WriteLine("  --run-ime-matching <PATH>         Parse IME logs and produce ime_pattern_matching.log");
            Console.WriteLine("                                    PATH = folder (all IME logs) or single log file");
            Console.WriteLine("  (default)                         Run enrollment monitoring");
            Console.WriteLine();
            Console.WriteLine("General options:");
            Console.WriteLine("  --help, -h, -?                    Show this help message");
            Console.WriteLine("  --console                         Enable console output (mirrors log to stdout)");
            Console.WriteLine("  --log-level <LEVEL>               Override log level (Info, Debug, Verbose, Trace)");
            Console.WriteLine("  --new-session                     Force a new session ID (delete persisted session)");
            Console.WriteLine("  --keep-logfile                    Preserve log directory after self-destruct cleanup");
            Console.WriteLine("  --no-cleanup                      Disable self-destruct on enrollment completion");
            Console.WriteLine("  --reboot-on-complete              Reboot the device after enrollment completes");
            Console.WriteLine("  --disable-geolocation             Skip geo-location detection");
            Console.WriteLine();
            Console.WriteLine("Authentication:");
            Console.WriteLine("  --no-auth                         Disable client cert auth (with --bootstrap-token only)");
            Console.WriteLine("  --cert-thumbprint <THUMBPRINT>    Use a specific certificate instead of auto-detection");
            Console.WriteLine("  --bootstrap-token <TOKEN>         Use bootstrap token auth (pre-MDM OOBE phase)");
            Console.WriteLine();
            Console.WriteLine("Await-enrollment mode:");
            Console.WriteLine("  --await-enrollment                Wait for MDM certificate before starting monitoring");
            Console.WriteLine("  --await-enrollment-timeout <MIN>  Timeout in minutes for await-enrollment (default: 480)");
            Console.WriteLine();
            Console.WriteLine("Overrides:");
            Console.WriteLine("  --tenant-id <ID>                  Override tenant ID (instead of registry discovery)");
            Console.WriteLine("  --api-url <URL>                   Override backend API base URL");
            Console.WriteLine("  --backend-api <URL>               Alias for --api-url");
            Console.WriteLine("  --ime-log-path <PATH>             Override IME logs directory");
            Console.WriteLine("  --ime-match-log <PATH>            Write matched IME log lines to file (debug)");
            Console.WriteLine();
            Console.WriteLine("IME matching options:");
            Console.WriteLine("  --patterns <FILE>                 Use local patterns JSON instead of GitHub");
            Console.WriteLine();
            Console.WriteLine("Replay (testing/simulation):");
            Console.WriteLine("  --replay-log-dir <PATH>           Directory with real IME logs for replay");
            Console.WriteLine("  --replay-speed-factor <FACTOR>    Time compression factor (default: 50)");
        }

        static string FormatArgsForLog(string[] args)
        {
            if (args == null || args.Length == 0)
                return "(none)";

            var parts = new string[args.Length];
            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0 && string.Equals(args[i - 1], "--bootstrap-token", StringComparison.OrdinalIgnoreCase))
                    parts[i] = "***REDACTED***";
                else
                    parts[i] = args[i];
            }

            return string.Join(" ", parts);
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

    /// <summary>
    /// Persisted await-enrollment configuration.
    /// Saved to %ProgramData%\AutopilotMonitor\await-enrollment.json by RunInstallMode
    /// so the Scheduled Task enters await-enrollment mode on startup.
    /// Deleted after the MDM certificate is found.
    /// </summary>
    class AwaitEnrollmentConfigFile
    {
        public int TimeoutMinutes { get; set; } = 480;
    }
}
