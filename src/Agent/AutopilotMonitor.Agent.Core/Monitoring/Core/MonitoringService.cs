using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.Core.Configuration;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Agent.Core.Monitoring.Analyzers;
using AutopilotMonitor.Agent.Core.Monitoring.Collectors;
using AutopilotMonitor.Agent.Core.Monitoring.Network;
using AutopilotMonitor.Agent.Core.Monitoring.Replay;
using AutopilotMonitor.Agent.Core.Monitoring.Tracking;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.Core.Monitoring.Core
{
    /// <summary>
    /// Main monitoring service that collects and uploads telemetry
    /// </summary>
    public partial class MonitoringService : IDisposable
    {
        private readonly AgentConfiguration _configuration;
        private readonly AgentLogger _logger;
        private readonly string _agentVersion;
        private readonly EventSpool _spool;
        private readonly BackendApiClient _apiClient;
        private readonly Timer _uploadTimer;
        private readonly Timer _debounceTimer;
        private readonly object _timerLock = new object();
        private readonly SemaphoreSlim _uploadSemaphore = new(1, 1);
        private readonly ManualResetEventSlim _completionEvent = new(false);
        private long _eventSequence; // Initialized from persistence + spool ceiling in constructor
        private readonly SessionPersistence _sessionPersistence;
        private EnrollmentPhase? _lastPhase = null;

        // Core event collectors (always on)
        private EspAndHelloTracker _espAndHelloTracker;
        private LogReplayService _logReplay;

        // Optional collectors (toggled via remote config)
        private PerformanceCollector _performanceCollector;
        private AgentSelfMetricsCollector _agentSelfMetricsCollector;

        // Smart enrollment tracking (replaces DownloadProgressCollector + EspUiStateCollector)
        private EnrollmentTracker _enrollmentTracker;

        // Remote config and gather rules
        private RemoteConfigService _remoteConfigService;
        private GatherRuleExecutor _gatherRuleExecutor;

        // Cleanup/self-destruct
        private readonly CleanupService _cleanupService;

        // Diagnostics package upload
        private readonly DiagnosticsPackageService _diagnosticsService;

        // Agent-side security and configuration analyzers
        private readonly List<IAgentAnalyzer> _analyzers = new List<IAgentAnalyzer>();

        // Collector idle timeout — stops periodic collectors when no real enrollment activity
        private DateTime _lastRealEventTime = DateTime.UtcNow;
        private bool _collectorsIdleStopped;
        private int _collectorIdleTimeoutMinutes = 15;
        private Timer _idleCheckTimer;

        // Desktop arrival detection (no-ESP scenarios)
        private DesktopArrivalDetector _desktopArrivalDetector;

        // Agent max lifetime safety net
        private Timer _maxLifetimeTimer;
        private readonly DateTime _agentStartTimeUtc = DateTime.UtcNow;
        private bool _enrollmentTerminalEventSeen;

        // Auth failure circuit breaker
        private int _consecutiveAuthFailures = 0;
        private DateTime? _firstAuthFailureTime = null;

        // Non-auth upload failure tracking for the emergency channel
        private int _consecutiveUploadFailures = 0;
        private EmergencyReporter _emergencyReporter;

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
            _emergencyReporter = new EmergencyReporter(_apiClient, _configuration.SessionId, _configuration.TenantId, agentVersion, _logger);
            _cleanupService = new CleanupService(_configuration, _logger);
            _diagnosticsService = new DiagnosticsPackageService(_configuration, _logger, _apiClient);

            // Initialize sequence from persistence, with spool-ceiling crash recovery
            var dataDirectory = Environment.ExpandEnvironmentVariables(Constants.AgentDataDirectory);
            _sessionPersistence = new SessionPersistence(dataDirectory);
            _eventSequence = _sessionPersistence.LoadSequence();
            var spoolMax = _spool.GetMaxSequence();
            if (spoolMax > _eventSequence)
            {
                _logger.Info($"Spool ceiling ({spoolMax}) > persisted sequence ({_eventSequence}), advancing");
                _eventSequence = spoolMax;
            }
            _logger.Info($"Event sequence initialized at {_eventSequence}");

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

            // WhiteGlove Part 2 detection: if the previous boot completed pre-provisioning,
            // emit a whiteglove_resumed event so the backend transitions Pending → InProgress.
            if (_sessionPersistence.IsWhiteGloveResume())
            {
                _logger.Info("WhiteGlove Part 2 detected — emitting whiteglove_resumed event");
                EmitEvent(new EnrollmentEvent
                {
                    SessionId = _configuration.SessionId,
                    TenantId = _configuration.TenantId,
                    EventType = "whiteglove_resumed",
                    Severity = EventSeverity.Info,
                    Source = "Agent",
                    Phase = EnrollmentPhase.Start,
                    Message = "WhiteGlove Part 2 — user enrollment started after pre-provisioning"
                });
                _sessionPersistence.ClearWhiteGloveComplete();
                _sessionPersistence.ResetSessionCreatedAt();
            }

            // Collect and emit device geo-location asynchronously — HTTP call, not on critical path
            if (_configuration.EnableGeoLocation)
            {
                Task.Run(EmitGeoLocationEvent);
            }

            // Start event collectors (HelloCollector + optional based on remote config)
            StartEventCollectors();

            // Start optional collectors based on remote config (PerformanceCollector)
            StartOptionalCollectors();

            // Start gather rule executor
            StartGatherRuleExecutor();

            // Initialize and run startup analyzers (security/configuration checks)
            InitializeAnalyzers();
            RunStartupAnalyzers();

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
                // Start ESP and Hello tracker (ESP exit, WhiteGlove, and WHfB provisioning tracking)
                var helloTimeout = _remoteConfigService?.CurrentConfig?.Collectors?.HelloWaitTimeoutSeconds
                    ?? _configuration.HelloWaitTimeoutSeconds;
                _espAndHelloTracker = new EspAndHelloTracker(
                    _configuration.SessionId,
                    _configuration.TenantId,
                    EmitEvent,
                    _logger,
                    helloTimeout
                );
                _espAndHelloTracker.Start();

                // Start log replay if a replay directory is configured
                if (!string.IsNullOrEmpty(_configuration.ReplayLogDir))
                {
                    _logger.Info($"Log replay mode enabled - starting log replay from: {_configuration.ReplayLogDir}");
                    var imeLogPatterns = _remoteConfigService?.CurrentConfig?.ImeLogPatterns;
                    _logReplay = new LogReplayService(
                        _configuration.SessionId,
                        _configuration.TenantId,
                        EmitEvent,
                        _logger,
                        _configuration.ReplayLogDir,
                        _configuration.ReplaySpeedFactor,
                        imeLogPatterns
                    );
                    _logReplay.Start();
                    _logger.Info("Log replay started");
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
                _remoteConfigService = new RemoteConfigService(_apiClient, _configuration.TenantId, _logger, _emergencyReporter);
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
            _configuration.SelfDestructOnComplete = config.SelfDestructOnComplete;
            _configuration.KeepLogFile = config.KeepLogFile;
            _configuration.EnableGeoLocation = config.EnableGeoLocation;

            // ImeMatchLog: use default path when enabled
            _configuration.ImeMatchLogPath = config.EnableImeMatchLog
                ? Environment.ExpandEnvironmentVariables(Constants.ImeMatchLogPath)
                : null;

            _configuration.MaxAuthFailures = config.MaxAuthFailures;
            _configuration.AuthFailureTimeoutMinutes = config.AuthFailureTimeoutMinutes;
            _configuration.RebootOnComplete = config.RebootOnComplete;
            _configuration.RebootDelaySeconds = config.RebootDelaySeconds;
            _configuration.MaxBatchSize = config.MaxBatchSize;
            _configuration.DiagnosticsUploadEnabled = config.DiagnosticsUploadEnabled;
            _configuration.DiagnosticsUploadMode = config.DiagnosticsUploadMode;
            _configuration.DiagnosticsLogPaths = config.DiagnosticsLogPaths ?? new System.Collections.Generic.List<AutopilotMonitor.Shared.Models.DiagnosticsLogPath>();

            // Apply log level from remote config
            if (Enum.TryParse<Logging.AgentLogLevel>(config.LogLevel, ignoreCase: true, out var remoteLogLevel))
            {
                _configuration.LogLevel = remoteLogLevel;
                _logger.SetLogLevel(remoteLogLevel);
            }

            _configuration.ShowEnrollmentSummary = config.ShowEnrollmentSummary;
            _configuration.EnrollmentSummaryTimeoutSeconds = config.EnrollmentSummaryTimeoutSeconds;
            _configuration.EnrollmentSummaryBrandingImageUrl = config.EnrollmentSummaryBrandingImageUrl;
            _configuration.SendTraceEvents = config.SendTraceEvents;
            _enrollmentTracker?.UpdateSendTraceEvents(config.SendTraceEvents);

            _logger.Info("Applied runtime settings from remote config");
            _logger.Info($"  uploadIntervalSeconds={_configuration.UploadIntervalSeconds}, selfDestructOnComplete={_configuration.SelfDestructOnComplete}, keepLogFile={_configuration.KeepLogFile}");
            _logger.Info($"  enableGeoLocation={_configuration.EnableGeoLocation}, imeMatchLogPath={_configuration.ImeMatchLogPath ?? "(disabled)"}");
            _logger.Info($"  maxAuthFailures={_configuration.MaxAuthFailures}, authFailureTimeoutMinutes={_configuration.AuthFailureTimeoutMinutes}");
            _logger.Info($"  logLevel={_configuration.LogLevel}, rebootOnComplete={_configuration.RebootOnComplete}, maxBatchSize={_configuration.MaxBatchSize}");
            _logger.Info($"  showEnrollmentSummary={_configuration.ShowEnrollmentSummary}, summaryTimeoutSeconds={_configuration.EnrollmentSummaryTimeoutSeconds}");
        }

        /// <summary>
        /// Starts optional collectors based on remote configuration
        /// </summary>
    }
}
