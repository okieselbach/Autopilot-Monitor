using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using AutopilotMonitor.Agent.Core.Configuration;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Agent.Core.Monitoring.Core;
using AutopilotMonitor.Shared;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent
{
    class Program
    {
        static void Main(string[] args)
        {
            // Install mode: ensure folders, deploy payload, create/update Scheduled Task, and start it.
            if (args.Contains("--install"))
            {
                RunInstallMode(args);
                return;
            }

            // Always run directly - no ServiceBase.Run.
            // The agent is started by the Scheduled Task (SYSTEM) or manually with --console.
            // Both paths land here and run identically; --console just enables console output.
            RunAgent(args);
        }

        static void RunInstallMode(string[] args)
        {
            var logDir = Environment.ExpandEnvironmentVariables(Constants.LogDirectory);
            var logger = new AgentLogger(logDir, AgentLogLevel.Debug);
            var consoleMode = args.Contains("--console") || Environment.UserInteractive;

            try
            {
                logger.Info("======================= Agent install mode (--install) =======================");

                EnsureAgentDirectories(logger);

                var sourceExePath = Assembly.GetExecutingAssembly().Location;
                var sourceDir = Path.GetDirectoryName(sourceExePath) ?? string.Empty;
                var targetAgentDir = Environment.ExpandEnvironmentVariables(Path.Combine(Constants.AgentDataDirectory, "Agent"));
                var targetExePath = Path.Combine(targetAgentDir, "AutopilotMonitor.Agent.exe");

                if (!string.Equals(
                    Path.GetFullPath(sourceDir).TrimEnd('\\'),
                    Path.GetFullPath(targetAgentDir).TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase))
                {
                    logger.Info($"Install mode called from '{sourceDir}'. Deploying payload to '{targetAgentDir}'.");
                    CopyDirectory(sourceDir, targetAgentDir, logger);
                }
                else
                {
                    logger.Info("Agent already running from target install directory; payload copy not required.");
                }

                var taskName = Constants.ScheduledTaskName;
                var taskCommand = $"\"{targetExePath}\"";

                logger.Info($"Registering Scheduled Task '{taskName}' for executable: {targetExePath}");

                var createExitCode = RunProcess(
                    "schtasks.exe",
                    $"/Create /TN \"{taskName}\" /TR \"{taskCommand}\" /SC ONSTART /RU SYSTEM /RL HIGHEST /F",
                    logger);

                if (createExitCode != 0)
                {
                    throw new InvalidOperationException($"Failed to create/update Scheduled Task '{taskName}' (exit code {createExitCode})");
                }

                logger.Info($"Scheduled Task '{taskName}' created/updated successfully");

                var startExitCode = RunProcess(
                    "schtasks.exe",
                    $"/Run /TN \"{taskName}\"",
                    logger);

                if (startExitCode != 0)
                {
                    throw new InvalidOperationException($"Failed to start Scheduled Task '{taskName}' (exit code {startExitCode})");
                }

                logger.Info($"Scheduled Task '{taskName}' started successfully");

                if (consoleMode)
                {
                    Console.WriteLine("Installation completed successfully.");
                    Console.WriteLine($"Task: {taskName}");
                    Console.WriteLine($"Executable: {targetExePath}");
                    Console.WriteLine($"Log: {logDir}");
                }

                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                logger.Error("Registration mode failed", ex);
                if (consoleMode)
                {
                    Console.WriteLine($"ERROR: {ex.Message}");
                }
                Environment.Exit(1);
            }
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

        static string GetTenantIdFromRegistry()
        {
            try
            {
                const string enrollmentsKeyPath = @"SOFTWARE\Microsoft\Enrollments";

                using (var enrollmentsKey = Registry.LocalMachine.OpenSubKey(enrollmentsKeyPath))
                {
                    if (enrollmentsKey == null)
                        return null;

                    foreach (var enrollmentGuid in enrollmentsKey.GetSubKeyNames())
                    {
                        using (var enrollmentKey = enrollmentsKey.OpenSubKey(enrollmentGuid))
                        {
                            if (enrollmentKey == null) continue;

                            var enrollmentType = enrollmentKey.GetValue("EnrollmentType");
                            if (enrollmentType != null && Convert.ToInt32(enrollmentType) == 6)
                            {
                                var tenantId = enrollmentKey.GetValue("AADTenantID");
                                if (tenantId != null)
                                    return tenantId.ToString();
                            }
                        }
                    }
                }
            }
            catch { }

            return null;
        }

        static AgentConfiguration LoadConfiguration(string[] args = null)
        {
            var noAuth            = args?.Contains("--no-auth") ?? false;
            var noCleanup         = args?.Contains("--no-cleanup") ?? false;
            var rebootOnComplete  = args?.Contains("--reboot-on-complete") ?? false;
            var disableGeoLoc     = args?.Contains("--disable-geolocation") ?? false;
            var newSession        = args?.Contains("--new-session") ?? false;
            var keepLogFile       = args?.Contains("--keep-logfile") ?? false;

            string certThumbprint        = null;
            string tenantIdOverride      = null;
            string apiBaseUrlOverride    = null;
            string imeLogPathOverride    = null;
            string imeMatchLogPath       = null;
            string replayLogDir      = null;
            double replaySpeedFactor = 50;

            if (args != null)
            {
                var thumbprintIndex = Array.IndexOf(args, "--cert-thumbprint");
                if (thumbprintIndex >= 0 && thumbprintIndex + 1 < args.Length)
                    certThumbprint = args[thumbprintIndex + 1];

                var tenantIdIndex = Array.IndexOf(args, "--tenant-id");
                if (tenantIdIndex >= 0 && tenantIdIndex + 1 < args.Length)
                    tenantIdOverride = args[tenantIdIndex + 1];

                // Local backend URL override for debugging/testing.
                // Supported aliases:
                // --api-url https://...
                // --backend-api https://...
                apiBaseUrlOverride = GetArgValue(args, "--api-url", "--backend-api");

                var imeLogPathIndex = Array.IndexOf(args, "--ime-log-path");
                if (imeLogPathIndex >= 0 && imeLogPathIndex + 1 < args.Length)
                    imeLogPathOverride = args[imeLogPathIndex + 1];

                var imeMatchLogIndex = Array.IndexOf(args, "--ime-match-log");
                if (imeMatchLogIndex >= 0 && imeMatchLogIndex + 1 < args.Length)
                    imeMatchLogPath = args[imeMatchLogIndex + 1];

                var replayLogDirIndex = Array.IndexOf(args, "--replay-log-dir");
                if (replayLogDirIndex >= 0 && replayLogDirIndex + 1 < args.Length)
                    replayLogDir = args[replayLogDirIndex + 1];

                var replaySpeedIndex = Array.IndexOf(args, "--replay-speed-factor");
                if (replaySpeedIndex >= 0 && replaySpeedIndex + 1 < args.Length)
                    if (double.TryParse(args[replaySpeedIndex + 1], out var speed))
                        replaySpeedFactor = speed;
            }

            // Defaults
            string apiBaseUrl            = Constants.ApiBaseUrl;
            int    uploadIntervalSeconds = Constants.DefaultUploadIntervalSeconds;
            bool   selfDestructOnComplete = false;
            bool   rebootOnCompleteConfig = false;
            bool   enableGeoLocationConfig = true;
            bool   useClientCertAuthConfig = true;

            // Environment variable overrides built-in default
            apiBaseUrl = Environment.GetEnvironmentVariable("AUTOPILOT_MONITOR_API") ?? apiBaseUrl;
            // CLI override wins over environment variable/default
            if (!string.IsNullOrWhiteSpace(apiBaseUrlOverride))
                apiBaseUrl = apiBaseUrlOverride;

            if (noCleanup)
            {
                selfDestructOnComplete = false;
            }

            // Determine TenantId: command-line arg > registry > fallback
            string tenantId;
            if (!string.IsNullOrEmpty(tenantIdOverride))
            {
                tenantId = tenantIdOverride;
            }
            else
            {
                tenantId = GetTenantIdFromRegistry();
            }

            var dataDirectory    = Environment.ExpandEnvironmentVariables(Constants.AgentDataDirectory);
            var sessionPersist   = new SessionPersistence(dataDirectory);

            if (newSession)
                sessionPersist.DeleteSession();

            var sessionId = sessionPersist.LoadOrCreateSessionId();

            return new AgentConfiguration
            {
                ApiBaseUrl            = apiBaseUrl,
                SessionId             = sessionId,
                TenantId              = tenantId,
                SpoolDirectory        = Environment.ExpandEnvironmentVariables(Constants.SpoolDirectory),
                LogDirectory          = Environment.ExpandEnvironmentVariables(Constants.LogDirectory),
                UploadIntervalSeconds = uploadIntervalSeconds,
                MaxBatchSize          = Constants.MaxBatchSize,
                LogLevel              = AgentLogLevel.Info,
                UseClientCertAuth     = !noAuth && useClientCertAuthConfig,
                ClientCertThumbprint  = certThumbprint,
                SelfDestructOnComplete = selfDestructOnComplete,
                RebootOnComplete      = rebootOnComplete || rebootOnCompleteConfig,
                EnableGeoLocation     = !disableGeoLoc && enableGeoLocationConfig,
                ImeLogPathOverride    = imeLogPathOverride,
                ImeMatchLogPath       = imeMatchLogPath,
                ReplayLogDir          = replayLogDir,
                ReplaySpeedFactor     = replaySpeedFactor,
                KeepLogFile           = keepLogFile
            };
        }

        private static string GetArgValue(string[] args, params string[] names)
        {
            if (args == null || names == null || names.Length == 0)
                return null;

            for (int i = 0; i < args.Length - 1; i++)
            {
                foreach (var name in names)
                {
                    if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    {
                        return args[i + 1];
                    }
                }
            }

            return null;
        }

        private static void EnsureAgentDirectories(AgentLogger logger)
        {
            var basePath = Environment.ExpandEnvironmentVariables(Constants.AgentDataDirectory);
            var paths = new[]
            {
                basePath,
                Path.Combine(basePath, "Agent"),
                Environment.ExpandEnvironmentVariables(Constants.SpoolDirectory),
                Environment.ExpandEnvironmentVariables(Constants.LogDirectory)
            };

            foreach (var path in paths)
            {
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                    logger.Info($"Created directory: {path}");
                }
                else
                {
                    logger.Debug($"Directory already exists: {path}");
                }
            }
        }

        private static int RunProcess(string fileName, string arguments, AgentLogger logger)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            logger.Info($"Executing: {fileName} {arguments}");

            using (var process = Process.Start(psi))
            {
                if (process == null)
                    throw new InvalidOperationException($"Failed to start process: {fileName}");

                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (!string.IsNullOrWhiteSpace(stdout))
                    logger.Info($"Process output: {stdout.Trim()}");
                if (!string.IsNullOrWhiteSpace(stderr))
                    logger.Warning($"Process error output: {stderr.Trim()}");

                logger.Info($"Process exit code: {process.ExitCode}");
                return process.ExitCode;
            }
        }

        private static void CopyDirectory(string sourceDir, string targetDir, AgentLogger logger)
        {
            if (!Directory.Exists(sourceDir))
                throw new DirectoryNotFoundException($"Source directory not found: {sourceDir}");

            Directory.CreateDirectory(targetDir);

            foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                var rel = dir.Substring(sourceDir.Length).TrimStart('\\');
                Directory.CreateDirectory(Path.Combine(targetDir, rel));
            }

            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var rel = file.Substring(sourceDir.Length).TrimStart('\\');
                var dest = Path.Combine(targetDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? targetDir);

                try
                {
                    File.Copy(file, dest, true);
                }
                catch (Exception ex)
                {
                    // If destination exists, keep going and let scheduled task use the latest available payload.
                    if (File.Exists(dest))
                    {
                        logger.Warning($"Could not overwrite '{dest}': {ex.Message}. Keeping existing file.");
                        continue;
                    }

                    throw;
                }
            }

            logger.Info($"Payload deployment completed: '{sourceDir}' -> '{targetDir}'");
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

        /// <summary>
        /// Checks for enrollment complete marker and handles cleanup retry if needed.
        /// Returns true if marker was found and agent should exit.
        /// </summary>
        static bool CheckEnrollmentCompleteMarker(AgentConfiguration config, AgentLogger logger, bool consoleMode)
        {
            var stateDirectory = Environment.ExpandEnvironmentVariables(Constants.StateDirectory);
            var markerPath = Path.Combine(stateDirectory, "enrollment-complete.marker");

            if (!File.Exists(markerPath))
            {
                // No marker - proceed with normal enrollment
                return false;
            }

            logger.Info("Enrollment complete marker detected from previous session");

            if (!config.SelfDestructOnComplete)
            {
                // No cleanup configured - just exit
                logger.Info("Enrollment already completed (SelfDestructOnComplete is disabled). Agent will exit.");
                if (consoleMode)
                    Console.WriteLine("Enrollment already completed (no cleanup configured). Agent will exit.");
                return true;
            }

            // Self-destruct is configured - attempt it now in case scheduled task failed
            logger.Info("Enrollment already completed. Attempting self-destruct retry (scheduled task may have failed)...");
            if (consoleMode)
                Console.WriteLine("Enrollment already completed. Attempting cleanup retry...");

            try
            {
                using (var service = new MonitoringService(config, logger, GetAgentVersion()))
                {
                    service.TriggerCleanup();
                }

                logger.Info("Cleanup retry completed. Agent will exit.");
                if (consoleMode)
                    Console.WriteLine("Cleanup retry completed. Agent will exit.");
            }
            catch (Exception ex)
            {
                logger.Warning($"Cleanup retry failed: {ex.Message}");
                if (consoleMode)
                    Console.WriteLine($"WARNING: Cleanup retry failed: {ex.Message}");
            }

            return true;
        }

        static string GetAgentVersion()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var informationalVersion = assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion;

                if (!string.IsNullOrWhiteSpace(informationalVersion))
                    return informationalVersion;

                var fileVersion = assembly
                    .GetCustomAttribute<AssemblyFileVersionAttribute>()
                    ?.Version;

                if (!string.IsNullOrWhiteSpace(fileVersion))
                    return fileVersion;

                return assembly.GetName().Version?.ToString() ?? "unknown";
            }
            catch
            {
                return "unknown";
            }
        }
    }
}
