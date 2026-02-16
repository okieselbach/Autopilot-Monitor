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
        private readonly string _agentVersion;
        private readonly EventSpool _spool;
        private readonly BackendApiClient _apiClient;
        private readonly Timer _uploadTimer;
        private readonly Timer _debounceTimer;
        private readonly object _timerLock = new object();
        private readonly ManualResetEventSlim _completionEvent = new(false);
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

        // Cleanup/self-destruct
        private readonly CleanupService _cleanupService;

        // Auth failure circuit breaker
        private int _consecutiveAuthFailures = 0;
        private DateTime? _firstAuthFailureTime = null;

        public MonitoringService(AgentConfiguration configuration, AgentLogger logger, string agentVersion)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _agentVersion = string.IsNullOrWhiteSpace(agentVersion) ? "unknown" : agentVersion;

            if (!_configuration.IsValid())
            {
                throw new InvalidOperationException("Invalid agent configuration");
            }

            _spool = new EventSpool(_configuration.SpoolDirectory);
            _apiClient = new BackendApiClient(_configuration.ApiBaseUrl, _configuration, _logger);
            _cleanupService = new CleanupService(_configuration, _logger);

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

            // Fetch remote config (collector toggles + gather rules) from backend
            FetchRemoteConfig();

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
                    { "agentVersion", _agentVersion }
                }
            });

            // Collect and emit device geo-location (if enabled)
            if (_configuration.EnableGeoLocation)
            {
                EmitGeoLocationEvent();
            }

            // Start event collectors (HelloCollector + optional based on remote config)
            StartEventCollectors();

            // Start optional collectors based on remote config (PerformanceCollector)
            StartOptionalCollectors();

            // Start gather rule executor
            StartGatherRuleExecutor();

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
                    _logger,
                    _configuration.HelloWaitTimeoutSeconds
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
                _remoteConfigService.FetchConfigAsync().Wait(TimeSpan.FromSeconds(15));
                ApplyRuntimeSettingsFromRemoteConfig();
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to fetch remote config (using defaults): {ex.Message}");
            }
        }

        private void ApplyRuntimeSettingsFromRemoteConfig()
        {
            var config = _remoteConfigService?.CurrentConfig;
            if (config == null) return;

            _configuration.UploadIntervalSeconds = config.UploadIntervalSeconds;
            _configuration.CleanupOnExit = config.CleanupOnExit;
            _configuration.SelfDestructOnComplete = config.SelfDestructOnComplete;
            _configuration.KeepLogFile = config.KeepLogFile;

            if (!string.IsNullOrWhiteSpace(config.ImeMatchLogPath))
            {
                _configuration.ImeMatchLogPath = config.ImeMatchLogPath;
            }

            _configuration.MaxAuthFailures = config.MaxAuthFailures;
            _configuration.AuthFailureTimeoutMinutes = config.AuthFailureTimeoutMinutes;
            _configuration.RebootOnComplete = config.RebootOnComplete;
            _configuration.MaxBatchSize = config.MaxBatchSize;

            // Apply log level from remote config
            if (Enum.TryParse<Logging.AgentLogLevel>(config.LogLevel, ignoreCase: true, out var remoteLogLevel))
            {
                _configuration.LogLevel = remoteLogLevel;
                _logger.SetLogLevel(remoteLogLevel);
            }

            _logger.Info("Applied runtime settings from remote config");
            _logger.Info($"  uploadIntervalSeconds={_configuration.UploadIntervalSeconds}, cleanupOnExit={_configuration.CleanupOnExit}, selfDestructOnComplete={_configuration.SelfDestructOnComplete}, keepLogFile={_configuration.KeepLogFile}");
            _logger.Info($"  imeMatchLogPath={_configuration.ImeMatchLogPath ?? "(none)"}");
            _logger.Info($"  maxAuthFailures={_configuration.MaxAuthFailures}, authFailureTimeoutMinutes={_configuration.AuthFailureTimeoutMinutes}");
            _logger.Info($"  logLevel={_configuration.LogLevel}, rebootOnComplete={_configuration.RebootOnComplete}, maxBatchSize={_configuration.MaxBatchSize}");
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
                    var imeMatchLogPath = string.IsNullOrEmpty(_configuration.ImeMatchLogPath)
                        ? null
                        : Environment.ExpandEnvironmentVariables(_configuration.ImeMatchLogPath);

                    _enrollmentTracker = new EnrollmentTracker(
                        _configuration.SessionId,
                        _configuration.TenantId,
                        EmitEvent,
                        _logger,
                        imeLogPatterns,
                        _configuration.ImeLogPathOverride,
                        imeMatchLogPath: imeMatchLogPath,
                        helloDetector: _helloDetector
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
                    SerialNumber = DeviceInfoProvider.GetSerialNumber(),
                    Manufacturer = DeviceInfoProvider.GetManufacturer(),
                    Model = DeviceInfoProvider.GetModel(),
                    DeviceName = Environment.MachineName,
                    OsBuild = Environment.OSVersion.Version.ToString(),
                    OsEdition = DeviceInfoProvider.GetOsEdition(),
                    OsLanguage = System.Globalization.CultureInfo.CurrentCulture.Name,
                    StartedAt = DateTime.UtcNow,
                    AgentVersion = _agentVersion,
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
            catch (BackendAuthException ex)
            {
                _logger.Error($"Session registration authentication failed: {ex.Message}");
                HandleAuthFailure();
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to register session", ex);
            }
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

            // Unblock WaitForCompletion() in background/SYSTEM mode
            _completionEvent.Set();
        }

        /// <summary>
        /// Blocks the calling thread until the service stops (used when running as Scheduled Task
        /// under SYSTEM where there is no interactive Console.ReadLine() to keep the process alive).
        /// </summary>
        public void WaitForCompletion()
        {
            _completionEvent.Wait();
        }

        /// <summary>
        /// Triggers cleanup without running enrollment monitoring.
        /// Used when enrollment complete marker is detected on startup (cleanup retry).
        /// </summary>
        public void TriggerCleanup()
        {
            _logger.Info("TriggerCleanup invoked - executing cleanup without enrollment monitoring");

            if (_configuration.SelfDestructOnComplete)
            {
                _cleanupService.ExecuteSelfDestruct();
            }
            else if (_configuration.CleanupOnExit)
            {
                _cleanupService.ExecuteCleanup();
            }
            else
            {
                _logger.Info("No cleanup configured - nothing to do");
            }
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

                    // Reset auth failure counter on success
                    _consecutiveAuthFailures = 0;
                    _firstAuthFailureTime = null;
                }
                else
                {
                    _logger.Warning($"Upload failed: {response.Message}");
                }
            }
            catch (BackendAuthException ex)
            {
                _logger.Error($"Upload authentication failed: {ex.Message}");
                HandleAuthFailure();
            }
            catch (Exception ex)
            {
                _logger.Error("Error uploading events", ex);
            }
        }

        /// <summary>
        /// Handles an authentication failure by incrementing the counter and shutting down
        /// the agent if the configured threshold is reached.
        /// </summary>
        private void HandleAuthFailure()
        {
            _consecutiveAuthFailures++;

            if (_firstAuthFailureTime == null)
                _firstAuthFailureTime = DateTime.UtcNow;

            _logger.Warning($"Authentication failure {_consecutiveAuthFailures}" +
                (_configuration.MaxAuthFailures > 0 ? $"/{_configuration.MaxAuthFailures}" : "") +
                $" (first failure at {_firstAuthFailureTime.Value:HH:mm:ss})");

            // Check max attempts (0 = disabled)
            if (_configuration.MaxAuthFailures > 0 && _consecutiveAuthFailures >= _configuration.MaxAuthFailures)
            {
                _logger.Error($"=== AGENT SHUTDOWN: {_consecutiveAuthFailures} consecutive authentication failures (401/403). " +
                    "The device is not authorized to send data to Autopilot Monitor. " +
                    "Check client certificate and serial number validation in your tenant configuration. ===");
                Environment.Exit(1);
            }

            // Check timeout (0 = disabled)
            if (_configuration.AuthFailureTimeoutMinutes > 0 && _firstAuthFailureTime.HasValue)
            {
                var elapsed = DateTime.UtcNow - _firstAuthFailureTime.Value;
                if (elapsed.TotalMinutes >= _configuration.AuthFailureTimeoutMinutes)
                {
                    _logger.Error($"=== AGENT SHUTDOWN: Authentication failures persisted for {elapsed.TotalMinutes:F0} minutes " +
                        $"(timeout: {_configuration.AuthFailureTimeoutMinutes} min). " +
                        "The device is not authorized to send data to Autopilot Monitor. " +
                        "Check client certificate and serial number validation in your tenant configuration. ===");
                    Environment.Exit(1);
                }
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
                    _cleanupService.ExecuteSelfDestruct();
                }
                else if (_configuration.CleanupOnExit)
                {
                    _cleanupService.ExecuteCleanup();
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
            _completionEvent.Dispose();
        }
    }
}
