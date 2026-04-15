using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AutopilotMonitor.Agent.Core.Configuration;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Agent.Core.Monitoring.Runtime;
using AutopilotMonitor.Shared;
using Microsoft.Win32;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Flows;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Completion;

namespace AutopilotMonitor.Agent
{
    partial class Program
    {
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
            var noCleanup         = args?.Contains("--no-cleanup") ?? false;
            var rebootOnComplete  = args?.Contains("--reboot-on-complete") ?? false;
            var disableGeoLoc     = args?.Contains("--disable-geolocation") ?? false;
            var newSession        = args?.Contains("--new-session") ?? false;
            var keepLogFile       = args?.Contains("--keep-logfile") ?? false;
            var awaitEnrollment   = args?.Contains("--await-enrollment") ?? false;

            string apiBaseUrlOverride    = null;
            string imeLogPathOverride    = null;
            string imeMatchLogPath       = null;
            string replayLogDir      = null;
            string bootstrapToken    = null;
            double replaySpeedFactor = 50;
            int    awaitEnrollmentTimeoutMinutes = 480;

            if (args != null)
            {
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

                var bootstrapTokenIndex = Array.IndexOf(args, "--bootstrap-token");
                if (bootstrapTokenIndex >= 0 && bootstrapTokenIndex + 1 < args.Length)
                    bootstrapToken = args[bootstrapTokenIndex + 1];

                var awaitTimeoutIndex = Array.IndexOf(args, "--await-enrollment-timeout");
                if (awaitTimeoutIndex >= 0 && awaitTimeoutIndex + 1 < args.Length)
                    if (int.TryParse(args[awaitTimeoutIndex + 1], out var awaitTimeout))
                        awaitEnrollmentTimeoutMinutes = awaitTimeout;
            }

            // Defaults
            string apiBaseUrl            = Constants.ApiBaseUrl;
            int    uploadIntervalSeconds = Constants.DefaultUploadIntervalSeconds;
            bool   selfDestructOnComplete = false;
            bool   rebootOnCompleteConfig = false;
            bool   enableGeoLocationConfig = true;
            bool   useClientCertAuthConfig = true;

            // CLI override wins over built-in default
            if (!string.IsNullOrWhiteSpace(apiBaseUrlOverride))
                apiBaseUrl = apiBaseUrlOverride;

            if (noCleanup)
            {
                selfDestructOnComplete = false;
            }

            // Determine TenantId from registry (or bootstrap-config.json fallback below)
            string tenantId = GetTenantIdFromRegistry();

            var dataDirectory    = Environment.ExpandEnvironmentVariables(Constants.AgentDataDirectory);
            var sessionPersist   = new SessionPersistence(dataDirectory);

            if (newSession)
                sessionPersist.DeleteSession();

            var sessionId = sessionPersist.LoadOrCreateSessionId();

            // Bootstrap config: if no --bootstrap-token CLI arg, check for persisted config file.
            // The OOBE bootstrap script calls --install --bootstrap-token {TOKEN} --tenant-id {TENANTID}.
            // RunInstallMode persists these to bootstrap-config.json so the Scheduled Task picks them up
            // on restart (the task command line has no args — just the exe path).
            var bootstrapConfigPath = Path.Combine(dataDirectory, "bootstrap-config.json");
            if (string.IsNullOrEmpty(bootstrapToken) && File.Exists(bootstrapConfigPath))
            {
                try
                {
                    var json = File.ReadAllText(bootstrapConfigPath);
                    var bootstrapConfig = Newtonsoft.Json.JsonConvert.DeserializeObject<BootstrapConfigFile>(json);
                    if (bootstrapConfig != null)
                    {
                        bootstrapToken = bootstrapConfig.BootstrapToken;
                        if (string.IsNullOrEmpty(tenantId) && !string.IsNullOrEmpty(bootstrapConfig.TenantId))
                        {
                            tenantId = bootstrapConfig.TenantId;
                        }
                    }
                }
                catch { /* ignore corrupt file */ }
            }

            // Await-enrollment config: if no --await-enrollment CLI arg, check for persisted config file.
            // RunInstallMode persists await-enrollment.json so the Scheduled Task picks it up on restart.
            var awaitConfigPath = Path.Combine(dataDirectory, "await-enrollment.json");
            if (!awaitEnrollment && File.Exists(awaitConfigPath))
            {
                try
                {
                    var json = File.ReadAllText(awaitConfigPath);
                    var awaitConfig = Newtonsoft.Json.JsonConvert.DeserializeObject<AwaitEnrollmentConfigFile>(json);
                    if (awaitConfig != null)
                    {
                        awaitEnrollment = true;
                        awaitEnrollmentTimeoutMinutes = awaitConfig.TimeoutMinutes;
                    }
                }
                catch { /* ignore corrupt file */ }
            }

            var useBootstrapToken = !string.IsNullOrEmpty(bootstrapToken);

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
                UseClientCertAuth     = !useBootstrapToken && useClientCertAuthConfig,
                SelfDestructOnComplete = selfDestructOnComplete,
                RebootOnComplete      = rebootOnComplete || rebootOnCompleteConfig,
                EnableGeoLocation     = !disableGeoLoc && enableGeoLocationConfig,
                ImeLogPathOverride    = imeLogPathOverride,
                ImeMatchLogPath       = imeMatchLogPath,
                ReplayLogDir          = replayLogDir,
                ReplaySpeedFactor     = replaySpeedFactor,
                KeepLogFile           = keepLogFile,
                BootstrapToken        = bootstrapToken,
                UseBootstrapTokenAuth = useBootstrapToken,
                AwaitEnrollment       = awaitEnrollment,
                AwaitEnrollmentTimeoutMinutes = awaitEnrollmentTimeoutMinutes,
                CommandLineArgs       = FormatArgsForLog(args)
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

        /// <summary>
        /// Checks for enrollment complete marker and handles cleanup retry if needed.
        /// Returns true if marker was found and agent should exit.
        /// </summary>
        static bool CheckEnrollmentCompleteMarker(AgentConfiguration config, AgentLogger logger, bool consoleMode)
        {
            // Registry guard: if "Deployed" key exists but no active session file,
            // this is a ghost restart after previous self-destruct. Exit immediately.
            try
            {
                using (var regKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\AutopilotMonitor"))
                {
                    var deployed = regKey?.GetValue("Deployed") as string;
                    if (!string.IsNullOrEmpty(deployed))
                    {
                        var dataDir = Environment.ExpandEnvironmentVariables(Constants.AgentDataDirectory);
                        var sessionPersistence = new SessionPersistence(dataDir);

                        if (!sessionPersistence.SessionExists())
                        {
                            logger.Info($"Agent was previously deployed ({deployed}) but no active session — ghost restart detected");

                            if (config.SelfDestructOnComplete)
                            {
                                logger.Info("Attempting cleanup retry (scheduled task may still be registered)...");
                                try
                                {
                                    using (var service = new MonitoringService(config, logger, GetAgentVersion()))
                                    {
                                        service.TriggerCleanup();
                                    }
                                    logger.Info("Cleanup retry completed.");
                                }
                                catch (Exception cleanupEx)
                                {
                                    logger.Warning($"Cleanup retry failed: {cleanupEx.Message}");
                                }
                            }

                            if (consoleMode)
                                Console.WriteLine("Agent was previously deployed but no active session. Exiting.");

                            return true;
                        }

                        // session.id exists → normal restart (Part 2, crash recovery) → continue
                        logger.Info($"Deployment marker found ({deployed}) with active session — continuing normally");
                    }
                }
            }
            catch (Exception regEx)
            {
                logger.Warning($"Registry deployment marker check failed: {regEx.Message}");
            }

            // File-based enrollment-complete marker (original guard)
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

        /// <summary>
        /// Emergency break: checks if the current session has been alive longer than the absolute maximum.
        /// Prevents zombie agents that stay on the device forever due to logic errors preventing
        /// normal enrollment completion. Respects WhiteGlove scenarios (skips check during resume).
        /// Returns true if emergency break triggered and agent should exit.
        /// </summary>
        static bool CheckSessionAgeEmergencyBreak(AgentConfiguration config, AgentLogger logger, bool consoleMode)
        {
            try
            {
                var dataDir = Environment.ExpandEnvironmentVariables(Constants.AgentDataDirectory);
                var persistence = new SessionPersistence(dataDir);

                // Skip check if WhiteGlove-paused (device powered off between Part 1 and Part 2).
                // The timer will be reset when MonitoringService.Start() processes the WhiteGlove resume.
                if (persistence.IsWhiteGloveResume())
                {
                    logger.Info("Emergency break: WhiteGlove resume detected — skipping session age check");
                    return false;
                }

                var sessionCreatedAt = persistence.LoadSessionCreatedAt();
                if (sessionCreatedAt == null)
                {
                    // Older agent without session.created — initialize the file now.
                    // The emergency break will only trigger on subsequent restarts.
                    if (persistence.SessionExists())
                    {
                        persistence.SaveSessionCreatedAt(DateTime.UtcNow);
                        logger.Info("Emergency break: Initialized session.created for existing session");
                    }
                    return false;
                }

                var sessionAgeHours = (DateTime.UtcNow - sessionCreatedAt.Value).TotalHours;
                var maxAgeHours = config.AbsoluteMaxSessionHours;

                if (sessionAgeHours <= maxAgeHours)
                {
                    logger.Info($"Emergency break: Session age {sessionAgeHours:F1}h within limit ({maxAgeHours}h)");
                    return false;
                }

                // Session too old — emergency self-destruct
                logger.Warning($"EMERGENCY BREAK: Session age {sessionAgeHours:F1}h exceeds maximum {maxAgeHours}h — forcing cleanup");

                if (consoleMode)
                    Console.WriteLine($"EMERGENCY: Session is {sessionAgeHours:F1}h old (max: {maxAgeHours}h). Forcing cleanup.");

                // Write enrollment-complete marker so next start exits cleanly
                var stateDir = Environment.ExpandEnvironmentVariables(Constants.StateDirectory);
                Directory.CreateDirectory(stateDir);
                var markerPath = Path.Combine(stateDir, "enrollment-complete.marker");
                File.WriteAllText(markerPath, $"Emergency break at {DateTime.UtcNow:O} (session age: {sessionAgeHours:F1}h)");

                if (config.SelfDestructOnComplete)
                {
                    using (var service = new MonitoringService(config, logger, GetAgentVersion()))
                    {
                        service.TriggerCleanup();
                    }
                }

                // Clean up session files
                persistence.DeleteSession();

                logger.Info("Emergency break: Cleanup completed. Agent will exit.");
                return true;
            }
            catch (Exception ex)
            {
                logger.Error($"Emergency break check failed: {ex.Message}", ex);
                return false; // Don't block startup on check failure
            }
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
