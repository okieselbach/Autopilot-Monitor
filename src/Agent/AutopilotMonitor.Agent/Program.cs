using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AutopilotMonitor.Agent.Core.Configuration;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Agent.Core.Monitoring.Core;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent
{
    class Program
    {
        static void Main(string[] args)
        {
            // Check for mass rollout simulator mode
            if (args.Contains("--simulator-mass-rollout"))
            {
                RunMassRolloutSimulator(args);
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
                var logger = new AgentLogger(logDir, enableDebug: config.EnableDebugLogging);

                logger.Info("");
                logger.Info("============================================================================================================");
                logger.Info($"============================== Agent starting ({(consoleMode ? "console" : "background/SYSTEM")}) ==============================");
                logger.Info("============================================================================================================");

                if (consoleMode)
                {
                    Console.WriteLine($"Session ID:  {config.SessionId}");
                    Console.WriteLine($"Tenant ID:   {config.TenantId}");
                    Console.WriteLine($"API URL:     {config.ApiBaseUrl}");
                    Console.WriteLine($"Log Dir:     {logDir}");
                    Console.WriteLine($"Keep Logs:   {config.KeepLogFile}");
                    if (config.EnableSimulator)
                    {
                        Console.WriteLine($"Simulator:   ENABLED (Failure={config.SimulateFailure})");
                        if (!string.IsNullOrEmpty(config.SimulationLogDirectory))
                            Console.WriteLine($"Sim Log Dir: {config.SimulationLogDirectory} ({config.SimulationSpeedFactor}x)");
                    }
                    if (config.RebootOnComplete) Console.WriteLine("Reboot:      ON COMPLETE");
                    if (config.UseClientCertAuth)
                    {
                        Console.WriteLine($"Cert Auth:   ENABLED");
                        if (!string.IsNullOrEmpty(config.ClientCertThumbprint))
                            Console.WriteLine($"Thumbprint:  {config.ClientCertThumbprint}");
                    }
                    Console.WriteLine();
                }

                using (var service = new MonitoringService(config, logger))
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
                        Environment.ExpandEnvironmentVariables(@"%ProgramData%\AutopilotMonitor\Logs"),
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

        static void RunMassRolloutSimulator(string[] args)
        {
            Console.WriteLine("Autopilot Monitor Agent - MASS ROLLOUT SIMULATOR MODE");
            Console.WriteLine("======================================================");
            Console.WriteLine("Simulating 5 parallel device enrollments across 3 tenants:");
            Console.WriteLine("  - 3x Tenant: b54dc1af-5320-4f60-b5d4-821e0cf2a359 (1 will fail)");
            Console.WriteLine("  - 1x Tenant: a53834b7-42bc-46a3-b004-369735c3acf9");
            Console.WriteLine("  - 1x Tenant: deadbeef-dead-beef-dead-beefdeadbeef");
            Console.WriteLine();

            // Parse base configuration from args (API URL, etc.)
            var baseConfig = LoadConfiguration(args);

            // Define the 5 device configurations
            var deviceConfigs = new[]
            {
                new { TenantId = "b54dc1af-5320-4f60-b5d4-821e0cf2a359", SimulateFailure = false, DeviceName = "Device-1" },
                new { TenantId = "b54dc1af-5320-4f60-b5d4-821e0cf2a359", SimulateFailure = true,  DeviceName = "Device-2" },
                new { TenantId = "b54dc1af-5320-4f60-b5d4-821e0cf2a359", SimulateFailure = false, DeviceName = "Device-3" },
                new { TenantId = "a53834b7-42bc-46a3-b004-369735c3acf9", SimulateFailure = false, DeviceName = "Device-4" },
                new { TenantId = "deadbeef-dead-beef-dead-beefdeadbeef", SimulateFailure = false, DeviceName = "Device-5" }
            };

            var tasks = new List<System.Threading.Tasks.Task>();
            var services = new List<MonitoringService>();

            for (int i = 0; i < deviceConfigs.Length; i++)
            {
                var deviceConfig = deviceConfigs[i];
                var instanceNumber = i + 1;

                var config = new AgentConfiguration
                {
                    ApiBaseUrl = baseConfig.ApiBaseUrl,
                    SessionId = Guid.NewGuid().ToString(),
                    TenantId = deviceConfig.TenantId,
                    SpoolDirectory = Environment.ExpandEnvironmentVariables($@"%ProgramData%\AutopilotMonitor\MassRollout\{deviceConfig.DeviceName}\Spool"),
                    LogDirectory = Environment.ExpandEnvironmentVariables($@"%ProgramData%\AutopilotMonitor\MassRollout\{deviceConfig.DeviceName}\Logs"),
                    UploadIntervalSeconds = 30,
                    MaxBatchSize = 100,
                    EnableDebugLogging = true,
                    EnableSimulator = true,
                    SimulateFailure = deviceConfig.SimulateFailure,
                    UseClientCertAuth = baseConfig.UseClientCertAuth,
                    ClientCertThumbprint = baseConfig.ClientCertThumbprint,
                    CleanupOnExit = false,
                    SelfDestructOnComplete = false,
                    RebootOnComplete = false,
                    EnableGeoLocation = false
                };

                var logDir = Environment.ExpandEnvironmentVariables(config.LogDirectory);
                Directory.CreateDirectory(config.SpoolDirectory);
                Directory.CreateDirectory(config.LogDirectory);

                var logger = new AgentLogger(logDir, enableDebug: true);

                Console.WriteLine($"[{deviceConfig.DeviceName}] Starting instance {instanceNumber}/5");
                Console.WriteLine($"  Tenant ID: {deviceConfig.TenantId}");
                Console.WriteLine($"  Session ID: {config.SessionId}");
                Console.WriteLine($"  Simulate Failure: {(deviceConfig.SimulateFailure ? "YES" : "NO")}");
                Console.WriteLine();

                var service = new MonitoringService(config, logger);
                services.Add(service);

                var task = System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        service.Start();
                        while (true)
                            System.Threading.Thread.Sleep(1000);
                    }
                    catch (Exception ex)
                    {
                        logger.Error($"[{deviceConfig.DeviceName}] Error in monitoring service", ex);
                        Console.WriteLine($"[{deviceConfig.DeviceName}] ERROR: {ex.Message}");
                    }
                });

                tasks.Add(task);
            }

            Console.WriteLine("All 5 instances started in parallel.");
            Console.WriteLine("Press Enter to stop all instances...");
            Console.ReadLine();

            Console.WriteLine();
            Console.WriteLine("Stopping all instances...");
            foreach (var service in services)
            {
                try { service.Stop(); service.Dispose(); }
                catch (Exception ex) { Console.WriteLine($"Error stopping service: {ex.Message}"); }
            }

            Console.WriteLine("All instances stopped.");
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
            var enableSimulator   = args?.Contains("--simulator") ?? false;
            var simulateFailure   = args?.Contains("--simulate-failure") ?? false;
            var noAuth            = args?.Contains("--no-auth") ?? false;
            var noCleanup         = args?.Contains("--no-cleanup") ?? false;
            var rebootOnComplete  = args?.Contains("--reboot-on-complete") ?? false;
            var disableGeoLoc     = args?.Contains("--disable-geolocation") ?? false;
            var newSession        = args?.Contains("--new-session") ?? false;
            var keepLogFile       = args?.Contains("--keep-logfile") ?? false;

            string certThumbprint        = null;
            string tenantIdOverride      = null;
            string imeLogPathOverride    = null;
            string imeMatchLogPath       = null;
            string simulationLogDir      = null;
            double simulationSpeedFactor = 50;

            if (args != null)
            {
                var thumbprintIndex = Array.IndexOf(args, "--cert-thumbprint");
                if (thumbprintIndex >= 0 && thumbprintIndex + 1 < args.Length)
                    certThumbprint = args[thumbprintIndex + 1];

                var tenantIdIndex = Array.IndexOf(args, "--tenant-id");
                if (tenantIdIndex >= 0 && tenantIdIndex + 1 < args.Length)
                    tenantIdOverride = args[tenantIdIndex + 1];

                var imeLogPathIndex = Array.IndexOf(args, "--ime-log-path");
                if (imeLogPathIndex >= 0 && imeLogPathIndex + 1 < args.Length)
                    imeLogPathOverride = args[imeLogPathIndex + 1];

                var imeMatchLogIndex = Array.IndexOf(args, "--ime-match-log");
                if (imeMatchLogIndex >= 0 && imeMatchLogIndex + 1 < args.Length)
                    imeMatchLogPath = args[imeMatchLogIndex + 1];

                var simLogDirIndex = Array.IndexOf(args, "--simulation-log-dir");
                if (simLogDirIndex >= 0 && simLogDirIndex + 1 < args.Length)
                    simulationLogDir = args[simLogDirIndex + 1];

                var simSpeedIndex = Array.IndexOf(args, "--simulation-speed-factor");
                if (simSpeedIndex >= 0 && simSpeedIndex + 1 < args.Length)
                    if (double.TryParse(args[simSpeedIndex + 1], out var speed))
                        simulationSpeedFactor = speed;
            }

            // Defaults
            string apiBaseUrl             = "https://autopilotmonitor-func.azurewebsites.net";
            int    uploadIntervalSeconds  = 30;
            bool   cleanupOnExit          = true;
            bool   selfDestructOnComplete = true;
            bool   rebootOnCompleteConfig = false;
            bool   enableGeoLocationConfig = true;
            bool   useClientCertAuthConfig = true;

            // Load configuration JSON written by PowerShell bootstrap script
            var configFilePath = Environment.ExpandEnvironmentVariables(@"%ProgramData%\AutopilotMonitor\Config\agent-config.json");
            if (File.Exists(configFilePath))
            {
                try
                {
                    var configJson = File.ReadAllText(configFilePath);
                    var configDict = JsonConvert.DeserializeObject<Dictionary<string, object>>(configJson);

                    if (configDict != null)
                    {
                        if (configDict.ContainsKey("apiBaseUrl") && configDict["apiBaseUrl"] != null)
                            apiBaseUrl = configDict["apiBaseUrl"].ToString();

                        if (configDict.ContainsKey("uploadIntervalSeconds") && configDict["uploadIntervalSeconds"] != null)
                            uploadIntervalSeconds = Convert.ToInt32(configDict["uploadIntervalSeconds"]);

                        if (configDict.ContainsKey("cleanupOnExit") && configDict["cleanupOnExit"] != null)
                            cleanupOnExit = Convert.ToBoolean(configDict["cleanupOnExit"]);

                        if (configDict.ContainsKey("selfDestructOnComplete") && configDict["selfDestructOnComplete"] != null)
                            selfDestructOnComplete = Convert.ToBoolean(configDict["selfDestructOnComplete"]);

                        if (configDict.ContainsKey("rebootOnComplete") && configDict["rebootOnComplete"] != null)
                            rebootOnCompleteConfig = Convert.ToBoolean(configDict["rebootOnComplete"]);

                        if (configDict.ContainsKey("enableGeoLocation") && configDict["enableGeoLocation"] != null)
                            enableGeoLocationConfig = Convert.ToBoolean(configDict["enableGeoLocation"]);

                        if (configDict.ContainsKey("useClientCertAuth") && configDict["useClientCertAuth"] != null)
                            useClientCertAuthConfig = Convert.ToBoolean(configDict["useClientCertAuth"]);

                        if (configDict.ContainsKey("keepLogFile") && configDict["keepLogFile"] != null)
                            keepLogFile = keepLogFile || Convert.ToBoolean(configDict["keepLogFile"]);

                        if (configDict.ContainsKey("imeMatchLogPath") && configDict["imeMatchLogPath"] != null)
                            imeMatchLogPath = configDict["imeMatchLogPath"].ToString();
                    }
                }
                catch { /* use defaults */ }
            }

            // Environment variable overrides config file
            apiBaseUrl = Environment.GetEnvironmentVariable("AUTOPILOT_MONITOR_API") ?? apiBaseUrl;

            if (noCleanup)
            {
                cleanupOnExit = false;
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
                if (string.IsNullOrEmpty(tenantId))
                    tenantId = "b54dc1af-5320-4f60-b5d4-821e0cf2a359"; // fallback for dev/testing
            }

            var dataDirectory    = Environment.ExpandEnvironmentVariables(@"%ProgramData%\AutopilotMonitor");
            var sessionPersist   = new SessionPersistence(dataDirectory);

            if (newSession)
                sessionPersist.DeleteSession();

            var sessionId = sessionPersist.LoadOrCreateSessionId();

            return new AgentConfiguration
            {
                ApiBaseUrl            = apiBaseUrl,
                SessionId             = sessionId,
                TenantId              = tenantId,
                SpoolDirectory        = Environment.ExpandEnvironmentVariables(@"%ProgramData%\AutopilotMonitor\Spool"),
                LogDirectory          = Environment.ExpandEnvironmentVariables(@"%ProgramData%\AutopilotMonitor\Logs"),
                UploadIntervalSeconds = uploadIntervalSeconds,
                MaxBatchSize          = 100,
                EnableDebugLogging    = true,
                EnableSimulator       = enableSimulator,
                SimulateFailure       = simulateFailure,
                UseClientCertAuth     = !noAuth && useClientCertAuthConfig,
                ClientCertThumbprint  = certThumbprint,
                CleanupOnExit         = cleanupOnExit,
                SelfDestructOnComplete = selfDestructOnComplete,
                RebootOnComplete      = rebootOnComplete || rebootOnCompleteConfig,
                EnableGeoLocation     = !disableGeoLoc && enableGeoLocationConfig,
                ImeLogPathOverride    = imeLogPathOverride,
                ImeMatchLogPath       = imeMatchLogPath,
                SimulationLogDirectory = simulationLogDir,
                SimulationSpeedFactor = simulationSpeedFactor,
                KeepLogFile           = keepLogFile
            };
        }
    }
}
