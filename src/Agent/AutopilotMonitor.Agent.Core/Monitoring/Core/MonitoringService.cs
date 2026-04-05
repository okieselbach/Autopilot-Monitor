using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
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
        private DeliveryOptimizationCollector _deliveryOptimizationCollector;

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

        // IME process watcher — detects when IntuneManagementExtension.exe exits
        private ImeProcessWatcher _imeProcessWatcher;

        // Network change detection (always on)
        private NetworkChangeDetector _networkChangeDetector;

        // Agent max lifetime safety net
        private Timer _maxLifetimeTimer;
        private readonly DateTime _agentStartTimeUtc = DateTime.UtcNow;
        private bool _enrollmentTerminalEventSeen;
        private bool _isWhiteGlovePart2;

        // Admin override detected during session registration (agent restart after admin action)
        private string _pendingAdminAction;

        // Previous exit classification (crash detection)
        private readonly string _previousExitType;     // "clean" | "exception_crash" | "hard_kill" | "reboot_kill" | "first_run"
        private readonly string _previousCrashException; // exception type name (only for exception_crash)
        private readonly DateTime? _lastBootTimeUtc;   // boot time from Event Log (only for reboot_kill)
        private long _persistedSequenceAtStartup;      // for detecting spool ceiling recovery

        // Auth failure circuit breaker
        private int _consecutiveAuthFailures = 0;
        private DateTime? _firstAuthFailureTime = null;

        // Non-auth upload failure tracking for the emergency channel
        private int _consecutiveUploadFailures = 0;
        private EmergencyReporter _emergencyReporter;
        private DistressReporter _distressReporter;

        // UnrestrictedMode audit: tracks whether the first config apply has happened
        private bool _isFirstConfigApply = true;
        private int? _deferredSecurityAuditConfigVersion; // deferred until after agent_started

        public MonitoringService(AgentConfiguration configuration, AgentLogger logger, string agentVersion,
            string previousExitType = "first_run", string previousCrashException = null,
            DateTime? lastBootTimeUtc = null)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _agentVersion = string.IsNullOrWhiteSpace(agentVersion) ? "unknown" : agentVersion;
            _previousExitType = previousExitType ?? "first_run";
            _previousCrashException = previousCrashException;
            _lastBootTimeUtc = lastBootTimeUtc;

            if (!_configuration.IsValid())
            {
                throw new InvalidOperationException("Invalid agent configuration");
            }

            _spool = new EventSpool(_configuration.SpoolDirectory);

            // DistressReporter is created BEFORE BackendApiClient so it's available even if cert loading fails.
            // It uses its own plain HttpClient (no cert, no mTLS) for the pre-auth distress endpoint.
            var hwInfo = Security.HardwareInfo.GetHardwareInfo(_logger);
            _distressReporter = new DistressReporter(
                _configuration.ApiBaseUrl, _configuration.TenantId,
                hwInfo.Manufacturer, hwInfo.Model, hwInfo.SerialNumber,
                _agentVersion, _logger);

            _apiClient = new BackendApiClient(_configuration.ApiBaseUrl, _configuration, _logger, _agentVersion);
            _emergencyReporter = new EmergencyReporter(_apiClient, _configuration.SessionId, _configuration.TenantId, agentVersion, _logger);

            // Detect cert-missing at construction time and send distress signal
            if (_configuration.UseClientCertAuth && _apiClient.ClientCertificate == null)
            {
                _ = _distressReporter.TrySendAsync(
                    DistressErrorType.AuthCertificateMissing,
                    "MDM certificate not found in LocalMachine or CurrentUser store");
            }
            _cleanupService = new CleanupService(_configuration, _logger);
            _diagnosticsService = new DiagnosticsPackageService(_configuration, _logger, _apiClient);

            // Initialize sequence from persistence, with spool-ceiling crash recovery
            var dataDirectory = Environment.ExpandEnvironmentVariables(Constants.AgentDataDirectory);
            _sessionPersistence = new SessionPersistence(dataDirectory);
            _eventSequence = _sessionPersistence.LoadSequence();
            _persistedSequenceAtStartup = _eventSequence;
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

            // Check if admin already terminated this session before we restarted
            if (!string.IsNullOrEmpty(_pendingAdminAction))
            {
                var succeeded = string.Equals(_pendingAdminAction, "Succeeded", StringComparison.OrdinalIgnoreCase);
                _logger.Warning($"=== ADMIN OVERRIDE on startup: Session already marked as {_pendingAdminAction} by administrator — running cleanup only ===");

                // Start spool watcher briefly so the terminal event gets spooled for upload
                _spool.StartWatching();

                EmitEvent(new EnrollmentEvent
                {
                    SessionId = _configuration.SessionId,
                    TenantId = _configuration.TenantId,
                    EventType = succeeded ? "enrollment_complete" : "enrollment_failed",
                    Severity = succeeded ? EventSeverity.Info : EventSeverity.Warning,
                    Source = "AdminOverride",
                    Phase = EnrollmentPhase.Complete,
                    Message = $"Session {_pendingAdminAction.ToLower()} by administrator (detected on restart) — cleanup initiated",
                    Timestamp = DateTime.UtcNow,
                    Data = new Dictionary<string, object>
                    {
                        { "adminAction", _pendingAdminAction }
                    }
                });
                // EmitEvent triggers HandleEnrollmentComplete which handles cleanup + exit
                return;
            }

            // Start FileSystemWatcher for efficient event detection
            _spool.StartWatching();
            _logger.Info("FileSystemWatcher started for efficient event upload");

            // Emit agent_started event with startup context so the portal shows how the agent was launched
            var spoolCeilingTriggered = _eventSequence > _persistedSequenceAtStartup;
            var startupData = new Dictionary<string, object>
            {
                { "agentVersion", _agentVersion },
                { "commandLineArgs", _configuration.CommandLineArgs ?? "(none)" },
                { "isBootstrapSession", _configuration.UseBootstrapTokenAuth },
                { "awaitEnrollment", _configuration.AwaitEnrollment },
                { "selfDestructOnComplete", _configuration.SelfDestructOnComplete },
                { "certAuth", _configuration.UseClientCertAuth },
                { "agentMaxLifetimeMinutes", _configuration.AgentMaxLifetimeMinutes },
                { "diagnosticsUploadMode", _configuration.DiagnosticsUploadMode ?? "Off" },
                { "previousExitType", _previousExitType },
                { "unsentSpoolEvents", _spool.GetCount() },
                { "spoolCeilingRecovery", spoolCeilingTriggered }
            };
            if (_previousCrashException != null)
                startupData["previousCrashException"] = _previousCrashException;

            EmitEvent(new EnrollmentEvent
            {
                SessionId = _configuration.SessionId,
                TenantId = _configuration.TenantId,
                EventType = "agent_started",
                Severity = EventSeverity.Info,
                Source = "Agent",
                Phase = EnrollmentPhase.Start,
                Message = "Autopilot Monitor Agent started",
                Data = startupData,
                ImmediateUpload = true
            });

            // If previous exit was caused by a system reboot, emit a timeline event for visibility
            if (_previousExitType == "reboot_kill" && _lastBootTimeUtc.HasValue)
            {
                var timeSinceBoot = DateTime.UtcNow - _lastBootTimeUtc.Value;
                var bootDisplay = timeSinceBoot.TotalSeconds < 120
                    ? $"{(int)timeSinceBoot.TotalSeconds}s"
                    : $"{(int)timeSinceBoot.TotalMinutes}min";

                EmitEvent(new EnrollmentEvent
                {
                    SessionId = _configuration.SessionId,
                    TenantId = _configuration.TenantId,
                    EventType = "system_reboot_detected",
                    Severity = EventSeverity.Info,
                    Source = "Agent",
                    // Phase intentionally left as Unknown — see EnrollmentEvent.Phase docs.
                    // Only phase-transition events set Phase; this is an informational event.
                    Message = $"System rebooted {bootDisplay} ago (boot: {_lastBootTimeUtc.Value:HH:mm:ss} UTC) — previous agent terminated by reboot",
                    Data = new Dictionary<string, object>
                    {
                        { "bootTimeUtc", _lastBootTimeUtc.Value.ToString("O") },
                        { "secondsSinceBoot", (int)timeSinceBoot.TotalSeconds },
                        { "previousExitType", "reboot_kill" }
                    },
                    ImmediateUpload = true
                });
            }

            // If the agent was self-updated on the previous run, emit a timeline event
            EmitSelfUpdateEventIfMarkerExists();

            // Emit deferred security_audit (initial UnrestrictedMode) now that agent_started has its sequence
            if (_deferredSecurityAuditConfigVersion.HasValue)
            {
                EmitEvent(new EnrollmentEvent
                {
                    SessionId = _configuration.SessionId,
                    TenantId = _configuration.TenantId,
                    EventType = "security_audit",
                    Severity = EventSeverity.Info,
                    Source = "MonitoringService",
                    Message = "UnrestrictedMode enabled (initial config)",
                    Data = new Dictionary<string, object>
                    {
                        ["unrestrictedMode"] = true,
                        ["source"] = "initial_config",
                        ["configVersion"] = _deferredSecurityAuditConfigVersion.Value
                    },
                    ImmediateUpload = true
                });
                _deferredSecurityAuditConfigVersion = null;
            }

            // WhiteGlove Part 2 detection: if the previous boot completed pre-provisioning,
            // emit a whiteglove_resumed event so the backend transitions Pending → InProgress.
            if (_sessionPersistence.IsWhiteGloveResume())
            {
                _isWhiteGlovePart2 = true;
                _logger.Info("WhiteGlove Part 2 detected — emitting whiteglove_resumed event");
                EmitEvent(new EnrollmentEvent
                {
                    SessionId = _configuration.SessionId,
                    TenantId = _configuration.TenantId,
                    EventType = "whiteglove_resumed",
                    Severity = EventSeverity.Info,
                    Source = "Agent",
                    Phase = EnrollmentPhase.Start,
                    Message = "WhiteGlove Part 2 — user enrollment started after pre-provisioning",
                    ImmediateUpload = true
                });
                _sessionPersistence.ClearWhiteGloveComplete();
                _sessionPersistence.ResetSessionCreatedAt();
            }

            // Collect and emit device geo-location asynchronously — HTTP call, not on critical path
            if (_configuration.EnableGeoLocation)
            {
                Task.Run(EmitGeoLocationEvent);
            }

            // NTP time check — diagnostic, not critical path
            Task.Run(EmitNtpTimeCheckEvent);

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
                var attempt = GeoLocationService.GetLocationAsync(_logger).Result;
                if (attempt.Location != null)
                {
                    EmitEvent(new EnrollmentEvent
                    {
                        SessionId = _configuration.SessionId,
                        TenantId = _configuration.TenantId,
                        EventType = "device_location",
                        Severity = EventSeverity.Info,
                        Source = "Network",
                        Phase = EnrollmentPhase.Unknown,
                        Message = $"Device location: {attempt.Location.City}, {attempt.Location.Region}, {attempt.Location.Country} (via {attempt.Location.Source})",
                        Data = attempt.Location.ToDictionary(),
                        ImmediateUpload = true
                    });
                }
                else
                {
                    // All providers failed — send trace event with error details for analysis
                    EmitEvent(new EnrollmentEvent
                    {
                        SessionId = _configuration.SessionId,
                        TenantId = _configuration.TenantId,
                        EventType = "agent_trace",
                        Severity = EventSeverity.Warning,
                        Source = "Network",
                        Phase = EnrollmentPhase.Unknown,
                        Message = "Geo-location lookup failed: all providers unreachable",
                        Data = new Dictionary<string, object>
                        {
                            { "decision", "geo_location_failed" },
                            { "reason", "All geo-location providers failed after retry" },
                            { "primaryError", attempt.PrimaryError ?? "unknown" },
                            { "primaryRetryError", attempt.PrimaryRetryError ?? "unknown" },
                            { "fallbackError", attempt.FallbackError ?? "unknown" },
                            { "primaryProvider", "ipinfo.io" },
                            { "fallbackProvider", "ifconfig.co" }
                        }
                    });
                }

                // Auto-set timezone if enabled and IANA timezone available from geolocation
                if (_configuration.EnableTimezoneAutoSet && !string.IsNullOrEmpty(attempt.Location?.Timezone))
                {
                    try
                    {
                        var tzResult = TimezoneService.TrySetTimezone(attempt.Location.Timezone, _logger);
                        EmitEvent(new EnrollmentEvent
                        {
                            SessionId = _configuration.SessionId,
                            TenantId = _configuration.TenantId,
                            EventType = "timezone_auto_set",
                            Severity = tzResult.Success ? EventSeverity.Info : EventSeverity.Warning,
                            Source = "Network",
                            Phase = EnrollmentPhase.Unknown,
                            Message = tzResult.Success
                                ? $"Timezone set to {tzResult.WindowsTimezoneId} (from {tzResult.IanaTimezone})"
                                : $"Timezone auto-set failed: {tzResult.Error}",
                            Data = new Dictionary<string, object>
                            {
                                { "ianaTimezone", tzResult.IanaTimezone ?? "" },
                                { "windowsTimezoneId", tzResult.WindowsTimezoneId ?? "unknown" },
                                { "previousTimezone", tzResult.PreviousTimezone ?? "unknown" },
                                { "success", tzResult.Success },
                                { "error", tzResult.Error ?? "" }
                            }
                        });
                    }
                    catch (Exception tzEx)
                    {
                        _logger.Warning($"Timezone auto-set failed: {tzEx.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to collect geo-location: {ex.Message}");
            }
        }

        private void EmitNtpTimeCheckEvent()
        {
            try
            {
                var ntpServer = string.IsNullOrEmpty(_configuration.NtpServer) ? "time.windows.com" : _configuration.NtpServer;
                var result = NtpTimeCheckService.CheckTime(ntpServer, _logger);

                if (result.Success)
                {
                    var severity = Math.Abs(result.OffsetSeconds) > 60
                        ? EventSeverity.Warning
                        : EventSeverity.Info;

                    EmitEvent(new EnrollmentEvent
                    {
                        SessionId = _configuration.SessionId,
                        TenantId = _configuration.TenantId,
                        EventType = "ntp_time_check",
                        Severity = severity,
                        Source = "Network",
                        Phase = EnrollmentPhase.Unknown,
                        Message = $"NTP time check: offset {result.OffsetSeconds:F2}s from {ntpServer}",
                        Data = new Dictionary<string, object>
                        {
                            { "ntpServer", ntpServer },
                            { "offsetSeconds", result.OffsetSeconds },
                            { "ntpTimeUtc", result.NtpTime?.ToString("o") ?? "" },
                            { "localTimeUtc", result.LocalTime?.ToString("o") ?? "" }
                        }
                    });
                }
                else
                {
                    EmitEvent(new EnrollmentEvent
                    {
                        SessionId = _configuration.SessionId,
                        TenantId = _configuration.TenantId,
                        EventType = "ntp_time_check",
                        Severity = EventSeverity.Warning,
                        Source = "Network",
                        Phase = EnrollmentPhase.Unknown,
                        Message = $"NTP time check failed: {result.Error}",
                        Data = new Dictionary<string, object>
                        {
                            { "ntpServer", ntpServer },
                            { "error", result.Error ?? "unknown" }
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"NTP time check failed: {ex.Message}");
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
                _remoteConfigService = new RemoteConfigService(_apiClient, _configuration.TenantId, _logger, _emergencyReporter, _distressReporter);
                _remoteConfigService.FetchConfigAsync().Wait(TimeSpan.FromSeconds(15));
                ApplyRuntimeSettingsFromRemoteConfig();
                VerifyAgentBinaryIntegrity();
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to fetch remote config (using defaults): {ex.Message}");
            }
        }

        /// <summary>
        /// Post-config integrity check: verifies that the running agent binary matches
        /// the SHA-256 hash provided by the backend (LatestAgentExeSha256).
        /// Closes the trust gap where the self-update at startup only had the version.json hash
        /// (same blob storage origin as the ZIP — single point of compromise).
        /// </summary>
        private void VerifyAgentBinaryIntegrity()
        {
            try
            {
                var expectedHash = _remoteConfigService?.CurrentConfig?.LatestAgentExeSha256;
                if (string.IsNullOrWhiteSpace(expectedHash))
                {
                    _logger.Debug("Post-config integrity check: no backend EXE hash available — skipping");
                    return;
                }

                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                {
                    _logger.Warning("Post-config integrity check: could not determine agent exe path");
                    return;
                }

                string actualHash;
                using (var sha256 = SHA256.Create())
                using (var stream = File.OpenRead(exePath))
                {
                    var hashBytes = sha256.ComputeHash(stream);
                    actualHash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
                }

                if (string.Equals(actualHash, expectedHash.ToLowerInvariant(), StringComparison.Ordinal))
                {
                    _logger.Info($"Post-config integrity check: SHA-256 verified OK ({actualHash.Substring(0, 12)}...)");
                }
                else
                {
                    _logger.Error($"Post-config integrity check: SHA-256 MISMATCH — expected={expectedHash.Substring(0, 12)}..., actual={actualHash.Substring(0, 12)}...");

                    _ = _emergencyReporter?.TrySendAsync(
                        AgentErrorType.IntegrityCheckFailed,
                        $"Binary hash mismatch: expected={expectedHash.Substring(0, 12)}..., actual={actualHash.Substring(0, 12)}...");
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Post-config integrity check failed: {ex.Message}");
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
                var oldLogLevel = _configuration.LogLevel;
                _configuration.LogLevel = remoteLogLevel;
                _logger.SetLogLevel(remoteLogLevel);
                if (oldLogLevel != remoteLogLevel)
                    _logger.Info($"Log level changed: {oldLogLevel} -> {remoteLogLevel}");
            }

            _configuration.ShowEnrollmentSummary = config.ShowEnrollmentSummary;
            _configuration.EnrollmentSummaryTimeoutSeconds = config.EnrollmentSummaryTimeoutSeconds;
            _configuration.EnrollmentSummaryBrandingImageUrl = config.EnrollmentSummaryBrandingImageUrl;
            _configuration.EnrollmentSummaryLaunchRetrySeconds = config.EnrollmentSummaryLaunchRetrySeconds;
            _configuration.NtpServer = config.NtpServer;
            _configuration.EnableTimezoneAutoSet = config.EnableTimezoneAutoSet;
            _configuration.SendTraceEvents = config.SendTraceEvents;
            AuditUnrestrictedModeChange(config);
            _enrollmentTracker?.UpdateSendTraceEvents(config.SendTraceEvents);

            _logger.Info("Applied runtime settings from remote config");
            _logger.Info($"  uploadIntervalSeconds={_configuration.UploadIntervalSeconds}, selfDestructOnComplete={_configuration.SelfDestructOnComplete}, keepLogFile={_configuration.KeepLogFile}");
            _logger.Info($"  enableGeoLocation={_configuration.EnableGeoLocation}, imeMatchLogPath={_configuration.ImeMatchLogPath ?? "(disabled)"}");
            _logger.Info($"  maxAuthFailures={_configuration.MaxAuthFailures}, authFailureTimeoutMinutes={_configuration.AuthFailureTimeoutMinutes}");
            _logger.Info($"  logLevel={_configuration.LogLevel}, rebootOnComplete={_configuration.RebootOnComplete}, maxBatchSize={_configuration.MaxBatchSize}");
            _logger.Info($"  showEnrollmentSummary={_configuration.ShowEnrollmentSummary}, summaryTimeoutSeconds={_configuration.EnrollmentSummaryTimeoutSeconds}");
            _logger.Info($"  ntpServer={_configuration.NtpServer}, enableTimezoneAutoSet={_configuration.EnableTimezoneAutoSet}");
        }

        private void AuditUnrestrictedModeChange(AgentConfigResponse config)
        {
            var previous = _configuration.UnrestrictedMode;
            _configuration.UnrestrictedMode = config.UnrestrictedMode;

            if (_isFirstConfigApply)
            {
                _isFirstConfigApply = false;
                if (config.UnrestrictedMode)
                {
                    // Defer initial security_audit until after agent_started so sequence numbers are correct
                    _logger.Info("UnrestrictedMode enabled via tenant configuration (initial config) — audit deferred until after agent_started");
                    _deferredSecurityAuditConfigVersion = config.ConfigVersion;
                }
            }
            else if (config.UnrestrictedMode != previous)
            {
                if (config.UnrestrictedMode)
                {
                    // Runtime activation — potential manipulation, emit Critical
                    _logger.Warning($"SECURITY: UnrestrictedMode changed to TRUE during runtime (configVersion={config.ConfigVersion})");
                    EmitEvent(new EnrollmentEvent
                    {
                        SessionId = _configuration.SessionId,
                        EventType = "security_audit",
                        Severity = EventSeverity.Critical,
                        Source = "MonitoringService",
                        Message = "UnrestrictedMode activated during runtime — potential config manipulation",
                        Data = new Dictionary<string, object>
                        {
                            ["unrestrictedMode"] = true,
                            ["previousValue"] = false,
                            ["source"] = "config_refresh",
                            ["configVersion"] = config.ConfigVersion
                        },
                        ImmediateUpload = true
                    });
                }
                else
                {
                    _logger.Info("UnrestrictedMode disabled during runtime");
                    EmitEvent(new EnrollmentEvent
                    {
                        SessionId = _configuration.SessionId,
                        EventType = "security_audit",
                        Severity = EventSeverity.Info,
                        Source = "MonitoringService",
                        Message = "UnrestrictedMode deactivated during runtime",
                        Data = new Dictionary<string, object>
                        {
                            ["unrestrictedMode"] = false,
                            ["previousValue"] = true,
                            ["source"] = "config_refresh",
                            ["configVersion"] = config.ConfigVersion
                        },
                        ImmediateUpload = true
                    });
                }
            }
        }

        /// <summary>
        /// Reads the self-update marker file left by SelfUpdater, emits an agent_self_updated event,
        /// and deletes the marker. Best-effort — failures are logged but never block startup.
        /// </summary>
        private void EmitSelfUpdateEventIfMarkerExists()
        {
            try
            {
                var markerPath = Environment.ExpandEnvironmentVariables(Constants.SelfUpdateMarkerFile);
                if (!File.Exists(markerPath))
                    return;

                var json = File.ReadAllText(markerPath);
                var marker = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                if (marker == null)
                {
                    TryDeleteMarker(markerPath);
                    return;
                }

                marker.TryGetValue("previousVersion", out var previousVersion);
                marker.TryGetValue("newVersion", out var newVersion);
                marker.TryGetValue("updatedAtUtc", out var updatedAtUtc);

                _logger.Info($"Self-update detected: {previousVersion} → {newVersion}");

                EmitEvent(new EnrollmentEvent
                {
                    SessionId = _configuration.SessionId,
                    TenantId = _configuration.TenantId,
                    EventType = Constants.EventTypes.AgentSelfUpdated,
                    Severity = EventSeverity.Info,
                    Source = "Agent",
                    Message = $"Agent self-updated from {previousVersion} to {newVersion}",
                    Data = new Dictionary<string, object>
                    {
                        { "previousVersion", previousVersion ?? "unknown" },
                        { "newVersion", newVersion ?? "unknown" },
                        { "updatedAtUtc", updatedAtUtc ?? "unknown" }
                    },
                    ImmediateUpload = true
                });

                TryDeleteMarker(markerPath);
            }
            catch (Exception ex)
            {
                _logger.Warning($"Could not process self-update marker: {ex.Message}");
            }
        }

        private static void TryDeleteMarker(string path)
        {
            try { File.Delete(path); } catch { }
        }

        /// <summary>
        /// Starts optional collectors based on remote configuration
        /// </summary>
    }
}
