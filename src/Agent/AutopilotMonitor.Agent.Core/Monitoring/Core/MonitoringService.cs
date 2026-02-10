using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.Core.Configuration;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Agent.Core.Monitoring.Collectors;
using AutopilotMonitor.Agent.Core.Monitoring.Network;
using AutopilotMonitor.Agent.Core.Monitoring.Simulation;
using AutopilotMonitor.Agent.Core.Monitoring.Tracking;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.Core.Monitoring.Core
{
    /// <summary>
    /// Main monitoring service that collects and uploads telemetry
    /// </summary>
    public class MonitoringService : IDisposable
    {
        private readonly AgentConfiguration _configuration;
        private readonly AgentLogger _logger;
        private readonly EventSpool _spool;
        private readonly BackendApiClient _apiClient;
        private readonly Timer _uploadTimer;
        private readonly Timer _debounceTimer;
        private readonly object _timerLock = new object();
        private long _eventSequence = 0;
        private EnrollmentPhase? _lastPhase = null;

        // Core event collectors (always on)
        private HelloDetector _helloDetector;
        private AutopilotSimulator _simulator;

        // Optional collectors (toggled via remote config)
        private PerformanceCollector _performanceCollector;

        // Smart enrollment tracking (replaces DownloadProgressCollector + EspUiStateCollector)
        private EnrollmentTracker _enrollmentTracker;

        // Remote config and gather rules
        private RemoteConfigService _remoteConfigService;
        private GatherRuleExecutor _gatherRuleExecutor;

        public MonitoringService(AgentConfiguration configuration, AgentLogger logger)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            if (!_configuration.IsValid())
            {
                throw new InvalidOperationException("Invalid agent configuration");
            }

            _spool = new EventSpool(_configuration.SpoolDirectory);
            _apiClient = new BackendApiClient(_configuration.ApiBaseUrl, _configuration, _logger);

            // Subscribe to spool events for batched upload when new events arrive
            _spool.EventsAvailable += OnEventsAvailable;

            // Set up debounce timer for batching (waits before uploading to collect more events)
            // This reduces API calls while still being responsive
            _debounceTimer = new Timer(
                DebounceTimerCallback,
                null,
                Timeout.Infinite, // Don't start initially
                Timeout.Infinite
            );

            // Set up periodic upload timer as fallback (much longer interval)
            // This ensures events are uploaded even if FileSystemWatcher misses something
            _uploadTimer = new Timer(
                UploadTimerCallback,
                null,
                TimeSpan.FromMinutes(1), // Initial delay
                TimeSpan.FromMinutes(5) // Fallback check every 5 minutes
            );

            _logger.Info("MonitoringService initialized with FileSystemWatcher and batching");
        }

        /// <summary>
        /// Starts the monitoring service
        /// </summary>
        public void Start()
        {
            _logger.Info("Starting monitoring service");

            // Register session with backend
            RegisterSessionAsync().Wait();

            // Start FileSystemWatcher for efficient event detection
            _spool.StartWatching();
            _logger.Info("FileSystemWatcher started for efficient event upload");

            // Emit agent_started event
            EmitEvent(new EnrollmentEvent
            {
                SessionId = _configuration.SessionId,
                TenantId = _configuration.TenantId,
                EventType = "agent_started",
                Severity = EventSeverity.Info,
                Source = "Agent",
                Phase = EnrollmentPhase.Start,
                Message = "Autopilot Monitor Agent started",
                Data = new Dictionary<string, object>
                {
                    { "agentVersion", "1.0.0" }
                }
            });

            // Collect and emit device geo-location (if enabled)
            if (_configuration.EnableGeoLocation)
            {
                EmitGeoLocationEvent();
            }

            // Note: Device info (network, DNS, proxy, Autopilot profile, AAD join, etc.)
            // is collected by EnrollmentTracker when it starts

            // Fetch remote config (collector toggles + gather rules) from backend
            FetchRemoteConfig();

            // Start event collectors (core + optional based on remote config)
            StartEventCollectors();

            // Start optional collectors based on remote config
            StartOptionalCollectors();

            // Start gather rule executor
            StartGatherRuleExecutor();

            // Start periodic config refresh
            _remoteConfigService?.StartPeriodicRefresh();

            _logger.Info("Monitoring service started");
        }

        private void EmitGeoLocationEvent()
        {
            try
            {
                var location = GeoLocationService.GetLocationAsync(_logger).Result;
                if (location != null)
                {
                    EmitEvent(new EnrollmentEvent
                    {
                        SessionId = _configuration.SessionId,
                        TenantId = _configuration.TenantId,
                        EventType = "device_location",
                        Severity = EventSeverity.Info,
                        Source = "Network",
                        Phase = EnrollmentPhase.Unknown,
                        Message = $"Device location: {location.City}, {location.Region}, {location.Country} (via {location.Source})",
                        Data = location.ToDictionary()
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to collect geo-location: {ex.Message}");
            }
        }

        /// <summary>
        /// Starts all event collection components
        /// </summary>
        private void StartEventCollectors()
        {
            _logger.Info("Starting event collectors");

            try
            {
                // Start Hello detector (WHfB provisioning tracking)
                _helloDetector = new HelloDetector(
                    _configuration.SessionId,
                    _configuration.TenantId,
                    EmitEvent,
                    _logger
                );
                _helloDetector.Start();

                // Start Autopilot Simulator if enabled
                if (_configuration.EnableSimulator)
                {
                    _logger.Info("Simulator mode enabled - starting Autopilot simulator");
                    var imeLogPatterns = _remoteConfigService?.CurrentConfig?.ImeLogPatterns;
                    _simulator = new AutopilotSimulator(
                        _configuration.SessionId,
                        _configuration.TenantId,
                        EmitEvent,
                        _logger,
                        _configuration.SimulateFailure,
                        _configuration.SimulationLogDirectory,
                        _configuration.SimulationSpeedFactor,
                        imeLogPatterns
                    );
                    _simulator.Start();
                    _logger.Info("Autopilot simulator started");
                }

                _logger.Info("Core event collectors started successfully");
            }
            catch (Exception ex)
            {
                _logger.Error("Error starting event collectors", ex);
            }
        }

        /// <summary>
        /// Fetches remote configuration from the backend API
        /// </summary>
        private void FetchRemoteConfig()
        {
            try
            {
                _remoteConfigService = new RemoteConfigService(_apiClient, _configuration.TenantId, _logger);
                _remoteConfigService.ConfigChanged += OnRemoteConfigChanged;
                _remoteConfigService.FetchConfigAsync().Wait(TimeSpan.FromSeconds(15));
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to fetch remote config (using defaults): {ex.Message}");
            }
        }

        /// <summary>
        /// Starts optional collectors based on remote configuration
        /// </summary>
        private void StartOptionalCollectors()
        {
            var config = _remoteConfigService?.CurrentConfig;
            var collectors = config?.Collectors;

            _logger.Info("Starting collectors based on remote config");

            try
            {
                // PerformanceCollector is always on (feeds UI chart)
                var perfInterval = collectors?.PerformanceIntervalSeconds ?? 60;
                _performanceCollector = new PerformanceCollector(
                    _configuration.SessionId,
                    _configuration.TenantId,
                    EmitEvent,
                    _logger,
                    perfInterval
                );
                _performanceCollector.Start();

                // EnrollmentTracker: smart enrollment tracking with IME log parsing
                // (replaces DownloadProgressCollector + EspUiStateCollector)
                if (!_configuration.EnableSimulator)
                {
                    var imeLogPatterns = config?.ImeLogPatterns;
                    _enrollmentTracker = new EnrollmentTracker(
                        _configuration.SessionId,
                        _configuration.TenantId,
                        EmitEvent,
                        _logger,
                        imeLogPatterns,
                        _configuration.ImeLogPathOverride
                    );
                    _enrollmentTracker.Start();
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Error starting optional collectors", ex);
            }
        }

        /// <summary>
        /// Stops all optional collectors
        /// </summary>
        private void StopOptionalCollectors()
        {
            _performanceCollector?.Stop();
            _performanceCollector?.Dispose();
            _performanceCollector = null;

            _enrollmentTracker?.Stop();
            _enrollmentTracker?.Dispose();
            _enrollmentTracker = null;
        }

        /// <summary>
        /// Starts the gather rule executor with rules from remote config
        /// </summary>
        private void StartGatherRuleExecutor()
        {
            var config = _remoteConfigService?.CurrentConfig;
            if (config?.GatherRules == null || config.GatherRules.Count == 0)
            {
                _logger.Info("No gather rules to execute");
                return;
            }

            try
            {
                _gatherRuleExecutor = new GatherRuleExecutor(
                    _configuration.SessionId,
                    _configuration.TenantId,
                    EmitEvent,
                    _logger,
                    _configuration.ImeLogPathOverride
                );
                _gatherRuleExecutor.UpdateRules(config.GatherRules);
            }
            catch (Exception ex)
            {
                _logger.Error("Error starting gather rule executor", ex);
            }
        }

        /// <summary>
        /// Called when remote configuration changes (periodic refresh detected a new version)
        /// Dynamically starts/stops optional collectors and updates gather rules
        /// </summary>
        private void OnRemoteConfigChanged(object sender, AgentConfigResponse newConfig)
        {
            _logger.Info("Remote config changed - updating collectors and rules");

            try
            {
                // Hot-reload IME log patterns without restart
                if (_enrollmentTracker != null && newConfig.ImeLogPatterns != null)
                {
                    _enrollmentTracker.UpdatePatterns(newConfig.ImeLogPatterns);
                }

                // Stop and restart optional collectors with new settings
                StopOptionalCollectors();
                StartOptionalCollectors();

                // Update gather rules
                if (_gatherRuleExecutor != null)
                {
                    _gatherRuleExecutor.UpdateRules(newConfig.GatherRules);
                }
                else
                {
                    StartGatherRuleExecutor();
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Error applying config changes", ex);
            }
        }

        /// <summary>
        /// Registers the session with the backend
        /// </summary>
        private async Task RegisterSessionAsync()
        {
            try
            {
                _logger.Info("Registering session with backend");

                var registration = new SessionRegistration
                {
                    SessionId = _configuration.SessionId,
                    TenantId = _configuration.TenantId,
                    SerialNumber = GetSerialNumber(),
                    Manufacturer = GetManufacturer(),
                    Model = GetModel(),
                    DeviceName = Environment.MachineName,
                    OsBuild = Environment.OSVersion.Version.ToString(),
                    OsEdition = GetOsEdition(),
                    OsLanguage = System.Globalization.CultureInfo.CurrentCulture.Name,
                    StartedAt = DateTime.UtcNow,
                    AgentVersion = "1.0.0",
                    EnrollmentType = EnrollmentTracker.DetectEnrollmentTypeStatic()
                };

                var response = await _apiClient.RegisterSessionAsync(registration);

                if (response.Success)
                {
                    _logger.Info($"Session registered successfully: {response.SessionId}");
                }
                else
                {
                    _logger.Warning($"Session registration failed: {response.Message}");
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to register session", ex);
            }
        }

        private string GetSerialNumber()
        {
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BIOS"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        return obj["SerialNumber"]?.ToString() ?? "Unknown";
                    }
                }
            }
            catch { }
            return "Unknown";
        }

        private string GetManufacturer()
        {
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher("SELECT Manufacturer FROM Win32_ComputerSystem"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        return obj["Manufacturer"]?.ToString() ?? "Unknown";
                    }
                }
            }
            catch { }
            return "Unknown";
        }

        private string GetModel()
        {
            try
            {
                var manufacturer = GetManufacturer();
                if (manufacturer.IndexOf("lenovo", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // Lenovo stores the friendly model name (e.g. "ThinkPad X1 Carbon Gen 9")
                    // in Win32_ComputerSystemProduct.Version instead of Win32_ComputerSystem.Model
                    // which only contains the internal type number (e.g. "20Y3S0FP00")
                    using (var searcher = new System.Management.ManagementObjectSearcher("SELECT Version FROM Win32_ComputerSystemProduct"))
                    {
                        foreach (var obj in searcher.Get())
                        {
                            return obj["Version"]?.ToString() ?? "Unknown";
                        }
                    }
                }

                using (var searcher = new System.Management.ManagementObjectSearcher("SELECT Model FROM Win32_ComputerSystem"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        return obj["Model"]?.ToString() ?? "Unknown";
                    }
                }
            }
            catch { }
            return "Unknown";
        }

        private string GetOsEdition()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    return key?.GetValue("EditionID")?.ToString() ?? "Unknown";
                }
            }
            catch { }
            return "Unknown";
        }

        /// <summary>
        /// Stops the monitoring service
        /// </summary>
        public void Stop()
        {
            _logger.Info("Stopping monitoring service");

            // Stop FileSystemWatcher
            _spool.StopWatching();

            // Stop event collectors
            StopEventCollectors();

            // Emit final event
            EmitEvent(new EnrollmentEvent
            {
                SessionId = _configuration.SessionId,
                TenantId = _configuration.TenantId,
                EventType = "agent_stopped",
                Severity = EventSeverity.Info,
                Source = "Agent",
                Phase = _lastPhase ?? EnrollmentPhase.Unknown,
                Message = "Autopilot Monitor Agent stopped"
            });

            // Final upload attempt
            UploadEventsAsync().Wait(TimeSpan.FromSeconds(10));

            _logger.Info("Monitoring service stopped");
        }

        /// <summary>
        /// Stops all event collection components
        /// </summary>
        private void StopEventCollectors()
        {
            _logger.Info("Stopping event collectors");

            try
            {
                _helloDetector?.Stop();
                _helloDetector?.Dispose();

                _simulator?.Stop();
                _simulator?.Dispose();

                // Stop optional collectors
                StopOptionalCollectors();

                // Stop gather rule executor
                _gatherRuleExecutor?.Dispose();
                _gatherRuleExecutor = null;

                // Stop remote config refresh
                _remoteConfigService?.StopPeriodicRefresh();

                _logger.Info("Event collectors stopped");
            }
            catch (Exception ex)
            {
                _logger.Error("Error stopping event collectors", ex);
            }
        }

        /// <summary>
        /// Emits an event (adds to spool)
        /// </summary>
        public void EmitEvent(EnrollmentEvent evt)
        {
            evt.Sequence = Interlocked.Increment(ref _eventSequence);
            _spool.Add(evt);
            _logger.Debug($"Event emitted: {evt.EventType} - {evt.Message}");

            // Check if this is a phase transition
            bool isPhaseTransition = false;
            if (evt.Phase != _lastPhase)
            {
                _logger.Debug($"Phase transition detected: {_lastPhase?.ToString() ?? "null"} -> {evt.Phase}");
                _lastPhase = evt.Phase;
                isPhaseTransition = true;

                // Notify gather rule executor of phase change
                try { _gatherRuleExecutor?.OnPhaseChanged(evt.Phase); } catch { }
            }

            // Notify gather rule executor of event type (for on_event triggers)
            if (!string.IsNullOrEmpty(evt.EventType))
            {
                try { _gatherRuleExecutor?.OnEvent(evt.EventType); } catch { }
            }

            // Check for enrollment completion events
            if (evt.EventType == "enrollment_complete" || evt.EventType == "enrollment_failed")
            {
                _logger.Info($"Enrollment completion detected: {evt.EventType}");

                // Delete session ID so a new session will be created on next enrollment
                DeleteSessionId();

                // Stop ALL collectors to minimize system impact
                _logger.Info("Stopping all collectors after enrollment completion...");
                StopEventCollectors();

                // Stop upload timers - no more periodic uploads needed
                _uploadTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                _debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);

                // Final upload to flush any remaining events
                Task.Run(async () =>
                {
                    try
                    {
                        await UploadEventsAsync();
                        _logger.Info("Final event upload after enrollment completion done");
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning($"Final upload after enrollment completion failed: {ex.Message}");
                    }
                });

                // Only trigger self-destruct if configured
                if (_configuration.SelfDestructOnComplete)
                {
                    Task.Run(() => HandleEnrollmentComplete());
                    return; // Don't continue with normal event processing
                }
            }

            // Immediate upload for:
            // 1. Critical events (errors) - for troubleshooting
            // 2. Phase transitions (start/end) - for real-time phase tracking in UI
            // 3. Events with "phase" in EventType - explicit phase-related events
            // 4. App download/install events - for real-time download progress UI updates
            var isAppEvent = evt.EventType?.StartsWith("app_", StringComparison.OrdinalIgnoreCase) == true;

            if (evt.Severity >= EventSeverity.Error ||
                isPhaseTransition ||
                evt.EventType?.Contains("phase", StringComparison.OrdinalIgnoreCase) == true ||
                isAppEvent)
            {
                _logger.Debug($"Critical/Phase/App event detected ({evt.EventType}), triggering immediate upload (bypassing debounce)");
                Task.Run(() => UploadEventsAsync());
            }
        }

        /// <summary>
        /// Called when new events are available in the spool (FileSystemWatcher detected new files)
        /// Uses debouncing to batch events before uploading
        /// </summary>
        private void OnEventsAvailable(object sender, EventArgs e)
        {
            _logger.Debug("FileSystemWatcher detected new events, starting/resetting debounce timer");

            // Reset debounce timer - wait for batch window before uploading
            // This allows multiple events to accumulate, reducing API calls
            lock (_timerLock)
            {
                _debounceTimer.Change(
                    TimeSpan.FromSeconds(_configuration.UploadIntervalSeconds),
                    Timeout.InfiniteTimeSpan
                );
            }
        }

        /// <summary>
        /// Called when debounce timer expires - uploads batched events
        /// </summary>
        private void DebounceTimerCallback(object state)
        {
            _logger.Debug("Debounce timer expired, uploading batched events");
            Task.Run(() => UploadEventsAsync());
        }

        /// <summary>
        /// Fallback timer callback in case FileSystemWatcher misses events
        /// </summary>
        private void UploadTimerCallback(object state)
        {
            Task.Run(() => UploadEventsAsync());
        }

        private async Task UploadEventsAsync()
        {
            try
            {
                var events = _spool.GetBatch(_configuration.MaxBatchSize);

                if (events.Count == 0)
                {
                    _logger.Debug("No events to upload");
                    return;
                }

                _logger.Debug($"Uploading {events.Count} events");

                var request = new IngestEventsRequest
                {
                    SessionId = _configuration.SessionId,
                    TenantId = _configuration.TenantId,
                    Events = events
                };

                var response = await _apiClient.IngestEventsAsync(request);

                if (response.Success)
                {
                    _spool.RemoveEvents(events);
                    _logger.Debug($"Successfully uploaded {response.EventsProcessed} events");
                }
                else
                {
                    _logger.Warning($"Upload failed: {response.Message}");
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Error uploading events", ex);
            }
        }

        /// <summary>
        /// Deletes the persisted session ID file
        /// Should be called when enrollment is complete/failed
        /// </summary>
        private void DeleteSessionId()
        {
            _logger.Info("Deleting persisted session ID...");
            try
            {
                var dataDirectory = Environment.ExpandEnvironmentVariables(@"%ProgramData%\AutopilotMonitor");
                var sessionPersistence = new SessionPersistence(dataDirectory);
                sessionPersistence.DeleteSession();
                _logger.Info("Session ID deleted successfully");
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to delete session ID: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles enrollment completion and triggers self-destruct sequence
        /// </summary>
        private async Task HandleEnrollmentComplete()
        {
            try
            {
                _logger.Info("===== ENROLLMENT COMPLETE - Starting Self-Destruct Sequence =====");

                // Step 1: Stop all event collectors
                _logger.Info("Stopping event collectors...");
                StopEventCollectors();
                _spool.StopWatching();

                // Step 2: Upload all remaining events
                _logger.Info("Uploading final events...");
                await UploadEventsAsync();

                // Give a moment for final upload to complete
                await Task.Delay(2000);

                // Step 3: Execute self-destruct or cleanup
                if (_configuration.SelfDestructOnComplete)
                {
                    ExecuteSelfDestruct();
                }
                else if (_configuration.CleanupOnExit)
                {
                    ExecuteCleanup();
                }
                else if (_configuration.RebootOnComplete)
                {
                    _logger.Info("Reboot on complete enabled - initiating reboot");
                    var psi = new ProcessStartInfo
                    {
                        FileName = "shutdown.exe",
                        Arguments = "/r /t 10 /c \"Autopilot enrollment completed - rebooting\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    Process.Start(psi);
                }

                _logger.Info("Self-destruct sequence initiated. Agent will now exit.");

                // Exit the application
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                _logger.Error("Error during self-destruct sequence", ex);
                Environment.Exit(1);
            }
        }

        /// <summary>
        /// Executes full self-destruct: removes scheduled task and deletes all files
        /// </summary>
        private void ExecuteSelfDestruct()
        {
            try
            {
                _logger.Info($"Executing FULL SELF-DESTRUCT (Scheduled Task + Files{(_configuration.RebootOnComplete ? " + Reboot" : "")})");

                var currentProcessId = Process.GetCurrentProcess().Id;
                var agentBasePath = Path.GetFullPath(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "AutopilotMonitor"
                ));

                // Create a self-deleting PowerShell script
                var cleanupScript = $@"
# Wait for agent process to exit
Start-Sleep -Seconds 3
Wait-Process -Id {currentProcessId} -ErrorAction SilentlyContinue -Timeout 30

# Remove Scheduled Task
Write-Host 'Removing Scheduled Task: {_configuration.ScheduledTaskName}'
try {{
    Stop-ScheduledTask -TaskName '{_configuration.ScheduledTaskName}' -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName '{_configuration.ScheduledTaskName}' -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host 'Scheduled Task removed successfully'
}} catch {{
    Write-Host ""Failed to remove Scheduled Task: $_""
}}

# Delete AutopilotMonitor folder
Write-Host 'Deleting folder: {agentBasePath}'
try {{
    Remove-Item -Path '{agentBasePath}' -Recurse -Force -ErrorAction Stop
    Write-Host 'Folder deleted successfully'
}} catch {{
    Write-Host ""Failed to delete folder: $_""
}}

Write-Host 'AutopilotMonitor Agent self-destruct complete'
{(_configuration.RebootOnComplete ? @"
# Reboot the device
Write-Host 'Initiating reboot...'
Restart-Computer -Force
" : "")}
";

                // Write cleanup script to temp location (outside of agent folder)
                var tempScriptPath = Path.Combine(Path.GetTempPath(), $"AutopilotMonitor-Cleanup-{Guid.NewGuid()}.ps1");
                File.WriteAllText(tempScriptPath, cleanupScript);
                _logger.Info($"Cleanup script written to {tempScriptPath}");

                // Launch cleanup script in background (detached process)
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{tempScriptPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
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
        private void ExecuteCleanup()
        {
            try
            {
                _logger.Info($"Executing CLEANUP (Files only, keeping Scheduled Task{(_configuration.RebootOnComplete ? " + Reboot" : "")})");

                var currentProcessId = Process.GetCurrentProcess().Id;
                var agentBasePath = Path.GetFullPath(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "AutopilotMonitor"
                ));

                // Create a cleanup PowerShell script
                var cleanupScript = $@"
# Wait for agent process to exit
Start-Sleep -Seconds 3
Wait-Process -Id {currentProcessId} -ErrorAction SilentlyContinue -Timeout 30

# Delete AutopilotMonitor folder
Write-Host 'Deleting folder: {agentBasePath}'
try {{
    Remove-Item -Path '{agentBasePath}' -Recurse -Force -ErrorAction Stop
    Write-Host 'Folder deleted successfully'
}} catch {{
    Write-Host ""Failed to delete folder: $_""
}}

Write-Host 'AutopilotMonitor Agent cleanup complete'
{(_configuration.RebootOnComplete ? @"
# Reboot the device
Write-Host 'Initiating reboot...'
Restart-Computer -Force
" : "")}
";

                // Write cleanup script to temp location
                var tempScriptPath = Path.Combine(Path.GetTempPath(), $"AutopilotMonitor-Cleanup-{Guid.NewGuid()}.ps1");
                File.WriteAllText(tempScriptPath, cleanupScript);
                _logger.Info($"Cleanup script written to {tempScriptPath}");

                // Launch cleanup script
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{tempScriptPath}\"",
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

        public void Dispose()
        {
            _uploadTimer?.Dispose();
            _debounceTimer?.Dispose();
            _apiClient?.Dispose();
            _spool?.Dispose();
            _helloDetector?.Dispose();
            _simulator?.Dispose();
            _performanceCollector?.Dispose();
            _enrollmentTracker?.Dispose();
            _gatherRuleExecutor?.Dispose();
            _remoteConfigService?.Dispose();
        }
    }
}
