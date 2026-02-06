using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using AutopilotMonitor.Agent.Core.Configuration;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Agent.Core.Monitoring;
using AutopilotMonitor.Agent.Core.Storage;
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

            // Check if running in console mode (for testing)
            if (args.Contains("--console") || Environment.UserInteractive)
            {
                RunConsole(args);
            }
            else
            {
                RunService();
            }
        }

        static void RunConsole(string[] args)
        {
            Console.WriteLine("Autopilot Monitor Agent - Console Mode");
            Console.WriteLine("======================================");
            Console.WriteLine();

            try
            {
                var config = LoadConfiguration(args);
                var logDir = Environment.ExpandEnvironmentVariables(config.LogDirectory);
                var logger = new AgentLogger(logDir, enableDebug: true);

                logger.Info("Agent starting in console mode");
                Console.WriteLine($"Session ID: {config.SessionId}");
                Console.WriteLine($"Tenant ID: {config.TenantId}");
                Console.WriteLine($"API URL: {config.ApiBaseUrl}");
                Console.WriteLine($"Log Directory: {logDir}");

                if (config.EnableSimulator)
                {
                    Console.WriteLine($"Simulator Mode: ENABLED");
                    Console.WriteLine($"Simulate Failure: {(config.SimulateFailure ? "YES" : "NO")}");
                }

                if (config.RebootOnComplete)
                {
                    Console.WriteLine($"Reboot on Complete: ENABLED");
                }

                if (config.EnableGeoLocation)
                {
                    Console.WriteLine($"GeoLocation Detection: ENABLED");
                }

                if (config.UseClientCertAuth)
                {
                    Console.WriteLine($"Client Certificate Auth: ENABLED");
                    if (!string.IsNullOrEmpty(config.ClientCertThumbprint))
                    {
                        Console.WriteLine($"Certificate Thumbprint: {config.ClientCertThumbprint}");
                    }
                }

                Console.WriteLine();

                using (var service = new MonitoringService(config, logger))
                {
                    service.Start();
                    Console.WriteLine("Agent is running. Press Enter to stop...");
                    Console.ReadLine();
                    service.Stop();
                }

                logger.Info("Agent stopped");
                Console.WriteLine("Agent stopped.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
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
                new { TenantId = "b54dc1af-5320-4f60-b5d4-821e0cf2a359", SimulateFailure = true, DeviceName = "Device-2" },  // This one will fail
                new { TenantId = "b54dc1af-5320-4f60-b5d4-821e0cf2a359", SimulateFailure = false, DeviceName = "Device-3" },
                new { TenantId = "a53834b7-42bc-46a3-b004-369735c3acf9", SimulateFailure = false, DeviceName = "Device-4" },
                new { TenantId = "deadbeef-dead-beef-dead-beefdeadbeef", SimulateFailure = false, DeviceName = "Device-5" }
            };

            var tasks = new List<System.Threading.Tasks.Task>();
            var services = new List<MonitoringService>();

            // Start all 5 instances in parallel
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
                    CleanupOnExit = false, // Don't cleanup in mass rollout mode for easier inspection
                    SelfDestructOnComplete = false, // Don't self-destruct in mass rollout mode
                    RebootOnComplete = false, // Don't reboot in mass rollout mode
                    EnableGeoLocation = false // Disable geo-location to reduce noise
                };

                var logDir = Environment.ExpandEnvironmentVariables(config.LogDirectory);

                // Ensure directories exist
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

                // Start each service in a separate task
                var task = System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        service.Start();

                        // Keep the service running until enrollment completes
                        // The simulator will automatically complete and emit enrollment_complete or enrollment_failed
                        while (true)
                        {
                            System.Threading.Thread.Sleep(1000);
                        }
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

            // Stop all services
            Console.WriteLine();
            Console.WriteLine("Stopping all instances...");
            foreach (var service in services)
            {
                try
                {
                    service.Stop();
                    service.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error stopping service: {ex.Message}");
                }
            }

            Console.WriteLine("All instances stopped.");
        }

        static void RunService()
        {
            var service = new AutopilotMonitorService();
            ServiceBase.Run(service);
        }

        /// <summary>
        /// Reads the Azure AD Tenant ID from the Windows Registry.
        /// Looks for enrollments with EnrollmentType = 6 (AAD Join).
        /// </summary>
        /// <returns>The AAD Tenant ID if found, otherwise null.</returns>
        static string GetTenantIdFromRegistry()
        {
            try
            {
                const string enrollmentsKeyPath = @"SOFTWARE\Microsoft\Enrollments";

                using (var enrollmentsKey = Registry.LocalMachine.OpenSubKey(enrollmentsKeyPath))
                {
                    if (enrollmentsKey == null)
                    {
                        Console.WriteLine("Registry key not found: HKLM\\" + enrollmentsKeyPath);
                        return null;
                    }

                    // Iterate through all enrollment GUIDs
                    foreach (var enrollmentGuid in enrollmentsKey.GetSubKeyNames())
                    {
                        using (var enrollmentKey = enrollmentsKey.OpenSubKey(enrollmentGuid))
                        {
                            if (enrollmentKey == null)
                                continue;

                            // Check if this is an AAD Join enrollment (EnrollmentType = 6)
                            var enrollmentType = enrollmentKey.GetValue("EnrollmentType");
                            if (enrollmentType != null && Convert.ToInt32(enrollmentType) == 6)
                            {
                                // Try to get AADTenantID
                                var tenantId = enrollmentKey.GetValue("AADTenantID");
                                if (tenantId != null)
                                {
                                    var tenantIdString = tenantId.ToString();
                                    Console.WriteLine($"Found AAD Tenant ID in registry: {tenantIdString} (Enrollment: {enrollmentGuid})");
                                    return tenantIdString;
                                }
                            }
                        }
                    }

                    Console.WriteLine("No AAD Join enrollment (EnrollmentType=6) with AADTenantID found in registry.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading Tenant ID from registry: {ex.Message}");
            }

            return null;
        }

        static AgentConfiguration LoadConfiguration(string[] args = null)
        {
            // Parse command-line arguments
            var enableSimulator = args?.Contains("--simulator") ?? false;
            var simulateFailure = args?.Contains("--simulate-failure") ?? false;
            var noAuth = args?.Contains("--no-auth") ?? false;
            var noCleanup = args?.Contains("--no-cleanup") ?? false;
            var rebootOnComplete = args?.Contains("--reboot-on-complete") ?? false;
            var disableGeoLocation = args?.Contains("--disable-geolocation") ?? false;

            // Parse certificate thumbprint if provided
            string certThumbprint = null;
            string tenantIdOverride = null;
            if (args != null)
            {
                var thumbprintIndex = Array.IndexOf(args, "--cert-thumbprint");
                if (thumbprintIndex >= 0 && thumbprintIndex + 1 < args.Length)
                {
                    certThumbprint = args[thumbprintIndex + 1];
                }

                var tenantIdIndex = Array.IndexOf(args, "--tenant-id");
                if (tenantIdIndex >= 0 && tenantIdIndex + 1 < args.Length)
                {
                    tenantIdOverride = args[tenantIdIndex + 1];
                }
            }

            // Defaults (fallback values if no config file exists)
            string apiBaseUrl = "http://localhost:7071";
            int uploadIntervalSeconds = 30;
            bool cleanupOnExit = true;
            bool selfDestructOnComplete = true;
            bool rebootOnCompleteConfig = false;
            bool enableGeoLocationConfig = true;
            bool useClientCertAuthConfig = true;

            // Try to load configuration from JSON file (written by PowerShell bootstrap script)
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

                        Console.WriteLine($"Loaded configuration from {configFilePath}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: Failed to load configuration file: {ex.Message}");
                    Console.WriteLine("Using default configuration");
                }
            }
            else
            {
                Console.WriteLine($"Configuration file not found at {configFilePath}");
                Console.WriteLine("Using default configuration");
            }

            // Environment variable overrides config file
            apiBaseUrl = Environment.GetEnvironmentVariable("AUTOPILOT_MONITOR_API") ?? apiBaseUrl;

            // Command-line arguments override everything (for --no-cleanup)
            if (noCleanup)
            {
                cleanupOnExit = false;
                selfDestructOnComplete = false;
            }

            // Determine TenantId: Command-line arg > Registry > Fallback
            string tenantId;
            if (!string.IsNullOrEmpty(tenantIdOverride))
            {
                // Command-line argument takes precedence
                tenantId = tenantIdOverride;
                Console.WriteLine($"Using Tenant ID from command-line argument: {tenantId}");
            }
            else
            {
                // Try to read from registry
                tenantId = GetTenantIdFromRegistry();

                if (string.IsNullOrEmpty(tenantId))
                {
                    // Fallback to default (for testing/development)
                    tenantId = "b54dc1af-5320-4f60-b5d4-821e0cf2a359";
                    Console.WriteLine($"Using fallback Tenant ID: {tenantId}");
                }
            }

            // Load or create persisted session ID
            var dataDirectory = Environment.ExpandEnvironmentVariables(@"%ProgramData%\AutopilotMonitor");
            var sessionPersistence = new SessionPersistence(dataDirectory);
            var sessionId = sessionPersistence.LoadOrCreateSessionId();

            var sessionStatus = sessionPersistence.SessionExists() && File.GetCreationTime(Path.Combine(dataDirectory, "session.id")) < DateTime.Now.AddMinutes(-1)
                ? "Restored from previous session"
                : "New session created";
            Console.WriteLine($"Session ID: {sessionId} ({sessionStatus})");

            return new AgentConfiguration
            {
                ApiBaseUrl = apiBaseUrl,
                SessionId = sessionId,
                TenantId = tenantId,
                SpoolDirectory = Environment.ExpandEnvironmentVariables(@"%ProgramData%\AutopilotMonitor\Spool"),
                LogDirectory = Environment.ExpandEnvironmentVariables(@"%ProgramData%\AutopilotMonitor\Logs"),
                UploadIntervalSeconds = uploadIntervalSeconds,
                MaxBatchSize = 100,
                EnableDebugLogging = true,
                EnableSimulator = enableSimulator,
                SimulateFailure = simulateFailure,
                UseClientCertAuth = !noAuth && useClientCertAuthConfig,
                ClientCertThumbprint = certThumbprint,
                CleanupOnExit = cleanupOnExit,
                SelfDestructOnComplete = selfDestructOnComplete,
                RebootOnComplete = rebootOnComplete || rebootOnCompleteConfig,
                EnableGeoLocation = !disableGeoLocation && enableGeoLocationConfig
            };
        }
    }
}
