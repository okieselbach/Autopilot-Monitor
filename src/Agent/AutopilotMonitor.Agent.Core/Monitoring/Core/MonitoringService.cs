using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.Core.Configuration;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Agent.Core.Monitoring.Network;
using AutopilotMonitor.Agent.Core.Monitoring.Tracking;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.Core.Monitoring.Core
{
    /// <summary>
    /// Thin coordinator for the Autopilot Monitor Agent. Delegates to:
    /// <see cref="EventUploadOrchestrator"/> — upload, retry, auth-failure circuit breaker.
    /// <see cref="CollectorCoordinator"/> — collector lifecycle, idle detection, analyzers.
    /// <see cref="EnrollmentCompletionHandler"/> — terminal event shutdown sequences.
    /// </summary>
    public class MonitoringService : IDisposable
    {
        private readonly AgentConfiguration _configuration;
        private readonly AgentLogger _logger;
        private readonly string _agentVersion;
        private readonly EventSpool _spool;
        private readonly BackendApiClient _apiClient;
        private readonly ManualResetEventSlim _completionEvent = new(false);
        private long _eventSequence; // Initialized from persistence + spool ceiling in constructor
        private readonly SessionPersistence _sessionPersistence;

        // Composed orchestrators (extracted from partial classes)
        private EventUploadOrchestrator _uploadOrchestrator;
        private CollectorCoordinator _collectorCoordinator;
        private EnrollmentCompletionHandler _completionHandler;
        private EnrollmentPhase? _lastPhase = null;


        // Remote config
        private RemoteConfigService _remoteConfigService;

        // Cleanup/self-destruct
        private readonly CleanupService _cleanupService;

        // Diagnostics package upload
        private readonly DiagnosticsPackageService _diagnosticsService;

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

        private EmergencyReporter _emergencyReporter;
        private DistressReporter _distressReporter;

        // UnrestrictedMode audit: tracks whether the first config apply has happened
        private bool _isFirstConfigApply = true;
        private int? _deferredSecurityAuditConfigVersion; // deferred until after agent_started

        // Runtime self-update trigger (hash mismatch recovery)
        // Set by the agent host (Program.cs) to inject the SelfUpdater implementation since
        // .Core cannot reference the .Agent exe project. Once-per-process guard via Interlocked.
        private static int _integrityUpdateAttempted; // 0 = not yet, 1 = attempted
        /// <summary>
        /// Callback invoked by the runtime hash-mismatch path to trigger a forced self-update.
        /// Host wires this to <c>SelfUpdater.CheckAndApplyUpdateAsync(..., forceUpdate: true,
        /// triggerReason: "runtime_hash_mismatch", downloadTimeoutMsOverride: 60_000)</c>.
        /// Receives the backend-provided ZIP SHA-256 hash so the updater can use it as the
        /// trusted-channel integrity source.
        /// </summary>
        public Func<string, Task> RuntimeSelfUpdateTriggerAsync { get; set; }

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

            // Create upload orchestrator (timers start when Start() is called)
            _uploadOrchestrator = new EventUploadOrchestrator(
                _configuration, _logger, _spool, _apiClient,
                _emergencyReporter, _distressReporter, _cleanupService,
                EmitEvent, EmitShutdownEvent,
                () => _enrollmentTerminalEventSeen);

            // Create enrollment completion handler
            _completionHandler = new EnrollmentCompletionHandler(
                _configuration, _logger, _agentVersion,
                EmitEvent, EmitShutdownEvent,
                () => _uploadOrchestrator.UploadEventsAsync(),
                () => _uploadOrchestrator.StopTimers(),
                _cleanupService, _diagnosticsService, _sessionPersistence, _spool);

            // Subscribe to spool events for batched upload
            _spool.EventsAvailable += _uploadOrchestrator.OnEventsAvailable;

            _logger.Info("MonitoringService initialized");
        }

        /// <summary>
        /// Starts the monitoring service
        /// </summary>
        public void Start()
        {
            _logger.Info("Starting monitoring service");

            // Fetch remote config (collector toggles + gather rules) from backend
            FetchRemoteConfig();

            // Create collector coordinator (needs RemoteConfigService from FetchRemoteConfig)
            _collectorCoordinator = new CollectorCoordinator(
                _configuration, _logger, _agentVersion,
                EmitEvent, EmitTraceEvent,
                _apiClient, _spool, _remoteConfigService,
                () => _enrollmentTerminalEventSeen,
                _previousExitType, _lastBootTimeUtc, _agentStartTimeUtc);

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
            _uploadOrchestrator.Start();
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

            // Consume self-update markers (updated / skipped / checked) left by SelfUpdater on the
            // previous startup and emit a single agent_version_check event. Dedup suppresses
            // repeated up_to_date events for the same latestVersion within the same session.
            EmitVersionCheckEventFromMarkers();

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
            _collectorCoordinator.StartEventCollectors();

            // Start optional collectors based on remote config (PerformanceCollector)
            _collectorCoordinator.StartOptionalCollectors();

            // Start gather rule executor
            _collectorCoordinator.StartGatherRuleExecutor();

            // Initialize and run startup analyzers (security/configuration checks)
            _collectorCoordinator.InitializeAnalyzers();
            _collectorCoordinator.RunStartupAnalyzers();

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

                    // Trigger a forced self-update on hash mismatch (once per process).
                    // Runs as fire-and-forget on a background task so monitoring keeps running —
                    // on success, SelfUpdater calls Environment.Exit via RestartAgent;
                    // on failure we log and continue with the stale binary until next restart.
                    TriggerSelfUpdateOnMismatch();
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Post-config integrity check failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Invokes the host-provided <see cref="RuntimeSelfUpdateTriggerAsync"/> delegate on a
        /// background task. Guarded by <c>_integrityUpdateAttempted</c> so we only try once
        /// per agent process (prevents download loops if the update repeatedly fails).
        /// </summary>
        private void TriggerSelfUpdateOnMismatch()
        {
            if (Interlocked.Exchange(ref _integrityUpdateAttempted, 1) == 1)
            {
                _logger.Info("Self-update: already attempted in this process — skipping");
                return;
            }

            if (RuntimeSelfUpdateTriggerAsync == null)
            {
                _logger.Warning("Self-update: no RuntimeSelfUpdateTriggerAsync wired by host — cannot recover from hash mismatch");
                return;
            }

            // Hand the ZIP hash from the current config to the updater as trusted-channel source.
            // Null is acceptable — SelfUpdater falls back to the version.json hash.
            var zipHash = _remoteConfigService?.CurrentConfig?.LatestAgentSha256;

            Task.Run(async () =>
            {
                try
                {
                    _logger.Info("Self-update: triggering forced update due to runtime hash mismatch");
                    await RuntimeSelfUpdateTriggerAsync(zipHash).ConfigureAwait(false);
                    _logger.Warning("Self-update: returned without restart — update did not apply, agent continues with stale binary");
                }
                catch (Exception ex)
                {
                    _logger.Error("Self-update: unexpected failure during forced update", ex);
                }
            });
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
            _collectorCoordinator?.UpdateSendTraceEvents(config.SendTraceEvents);

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
        /// Consumes any self-update markers left by SelfUpdater and emits a single
        /// <c>agent_version_check</c> event. Three marker variants map to the outcomes:
        /// <list type="bullet">
        /// <item><description>self-update-info.json → outcome=updated</description></item>
        /// <item><description>self-update-skipped.json → outcome=skipped or check_failed</description></item>
        /// <item><description>self-update-checked.json → outcome=up_to_date (subject to session-scoped dedup)</description></item>
        /// </list>
        /// Best-effort — failures are logged but never block startup. See
        /// <see cref="VersionCheckEventBuilder"/> for the pure logic + dedup rules.
        /// </summary>
        private void EmitVersionCheckEventFromMarkers()
        {
            var result = VersionCheckEventBuilder.TryBuild(
                _configuration.SessionId,
                _configuration.TenantId,
                _agentStartTimeUtc);

            if (!string.IsNullOrEmpty(result.ParseError))
            {
                _logger.Warning($"Could not process version-check marker: {result.ParseError}");
                return;
            }

            if (result.Deduped)
            {
                _logger.Verbose($"agent_version_check skipped (dedup: up_to_date, same session, same latestVersion)");
                return;
            }

            if (result.Event != null)
            {
                _logger.Info($"Agent version check: outcome={result.Outcome}");
                EmitEvent(result.Event);
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // Event emission (central nerve — all collectors route through here)
        // ──────────────────────────────────────────────────────────────────

        public void EmitEvent(EnrollmentEvent evt)
        {
            evt.Sequence = Interlocked.Increment(ref _eventSequence);
            _spool.Add(evt);
            if (evt.Severity <= EventSeverity.Debug)
                _logger.Verbose($"Event emitted: {evt.EventType} - {evt.Message}");
            else
                _logger.Info($"Event emitted: {evt.EventType} - {evt.Message}");

            // Track real enrollment activity for idle timeout
            if (!string.IsNullOrEmpty(evt.EventType) && !CollectorCoordinator.IsPeriodicEvent(evt.EventType))
            {
                _collectorCoordinator?.OnRealEventReceived();
            }

            // Persist sequence periodically (every 50 events) + always on critical events
            if (evt.Sequence % 50 == 0 ||
                evt.EventType == "whiteglove_complete" ||
                evt.EventType == "enrollment_complete" ||
                evt.EventType == "enrollment_failed")
            {
                _sessionPersistence.SaveSequence(evt.Sequence);
            }

            // Cancel max lifetime timer on terminal events
            if (evt.EventType == "whiteglove_complete" ||
                evt.EventType == "enrollment_complete" ||
                evt.EventType == "enrollment_failed")
            {
                _enrollmentTerminalEventSeen = true;
                _collectorCoordinator?.CancelMaxLifetimeTimer();
            }

            // Track phase transitions for logging and gather rule notifications
            if (evt.Phase != _lastPhase)
            {
                if (evt.Phase == EnrollmentPhase.Unknown)
                    _logger.Debug($"Phase transition: {_lastPhase?.ToString() ?? "null"} -> {evt.Phase}");
                else
                    _logger.Info($"Phase transition: {_lastPhase?.ToString() ?? "null"} -> {evt.Phase}");
                _lastPhase = evt.Phase;

                _collectorCoordinator?.OnPhaseChanged(evt.Phase);
            }

            // Notify gather rule executor of event type (for on_event triggers)
            if (!string.IsNullOrEmpty(evt.EventType))
            {
                _collectorCoordinator?.OnEvent(evt.EventType);
            }

            // Check for WhiteGlove completion — agent exits gracefully, session stays open
            if (evt.EventType == "whiteglove_complete")
            {
                _logger.Info("WhiteGlove pre-provisioning complete. Starting graceful shutdown sequence.");

                _collectorCoordinator.StopPerformanceCollectorOnly();
                _collectorCoordinator.StopEventCollectors();
                _spool.StopWatching();
                _uploadOrchestrator.StopTimers();

                Task.Run(() => _completionHandler.HandleWhiteGloveComplete(
                    () => _collectorCoordinator.StopEventCollectors(),
                    part => _collectorCoordinator.RunShutdownAnalyzers(part),
                    () => Interlocked.Read(ref _eventSequence)));

                return;
            }

            // Check for enrollment completion events
            if (evt.EventType == "enrollment_complete" || evt.EventType == "enrollment_failed")
            {
                var enrollmentSucceeded = evt.EventType == "enrollment_complete";
                _logger.Info($"Enrollment completion detected: {evt.EventType}");

                _completionHandler.DeleteSessionId();

                _logger.Info("Stopping all collectors after enrollment completion...");
                _collectorCoordinator.StopEventCollectors();
                _uploadOrchestrator.StopTimers();

                Task.Run(async () =>
                {
                    try
                    {
                        await _uploadOrchestrator.UploadEventsAsync();
                        _logger.Info("Final event upload after enrollment completion done");
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning($"Final upload after enrollment completion failed: {ex.Message}");
                    }
                });

                Task.Run(() => _completionHandler.HandleEnrollmentComplete(
                    enrollmentSucceeded, _isWhiteGlovePart2,
                    () => _collectorCoordinator.StopEventCollectors(),
                    part => _collectorCoordinator.RunShutdownAnalyzers(part)));
                return;
            }

            // Immediate upload when explicitly requested by the emitter, or as a safety net for
            // unhandled errors. All other events batch via the debounce timer.
            if (evt.ImmediateUpload || evt.Severity >= EventSeverity.Error)
            {
                _logger.Info($"Triggering immediate upload for {evt.EventType} (bypassing debounce)");
                Task.Run(() => _uploadOrchestrator.UploadEventsAsync());
            }
        }

        private void EmitTraceEvent(string source, string decision, string reason, Dictionary<string, object> context = null)
        {
            _logger.Trace($"{source}: {decision} — {reason}");

            if (!_configuration.SendTraceEvents)
                return;

            var data = new Dictionary<string, object>
            {
                { "decision", decision },
                { "reason", reason }
            };
            if (context != null)
            {
                foreach (var kvp in context)
                    data[kvp.Key] = kvp.Value;
            }

            EmitEvent(new EnrollmentEvent
            {
                SessionId = _configuration.SessionId,
                TenantId = _configuration.TenantId,
                EventType = "agent_trace",
                Severity = EventSeverity.Trace,
                Source = source,
                Phase = EnrollmentPhase.Unknown,
                Message = $"{decision}: {reason}",
                Data = data
            });
        }

        private void EmitShutdownEvent(string reason, string message, Dictionary<string, object> extraData = null)
        {
            var data = new Dictionary<string, object>
            {
                { "reason", reason },
                { "agentVersion", _agentVersion },
                { "uptimeMinutes", Math.Round((DateTime.UtcNow - _agentStartTimeUtc).TotalMinutes, 1) }
            };
            if (extraData != null)
            {
                foreach (var kvp in extraData)
                    data[kvp.Key] = kvp.Value;
            }

            EmitEvent(new EnrollmentEvent
            {
                SessionId = _configuration.SessionId,
                TenantId = _configuration.TenantId,
                Timestamp = DateTime.UtcNow,
                EventType = "agent_shutdown",
                Severity = EventSeverity.Info,
                Source = "Agent",
                Phase = _lastPhase ?? EnrollmentPhase.Unknown,
                Message = message,
                Data = data
            });
        }

        // ──────────────────────────────────────────────────────────────────
        // Session registration
        // ──────────────────────────────────────────────────────────────────

        private async Task RegisterSessionAsync()
        {
            const int maxAttempts = 5;

            var registration = new SessionRegistration
            {
                SessionId = _configuration.SessionId,
                TenantId = _configuration.TenantId,
                SerialNumber = DeviceInfoProvider.GetSerialNumber(),
                Manufacturer = DeviceInfoProvider.GetManufacturer(),
                Model = DeviceInfoProvider.GetModel(),
                DeviceName = Environment.MachineName,
                OsName = DeviceInfoProvider.GetOsName(),
                OsBuild = DeviceInfoProvider.GetOsBuild(),
                OsDisplayVersion = DeviceInfoProvider.GetOsDisplayVersion(),
                OsEdition = DeviceInfoProvider.GetOsEdition(),
                OsLanguage = System.Globalization.CultureInfo.CurrentCulture.Name,
                StartedAt = DateTime.UtcNow,
                AgentVersion = _agentVersion,
                EnrollmentType = EnrollmentTracker.DetectEnrollmentTypeStatic(),
                IsHybridJoin = EnrollmentTracker.DetectHybridJoinStatic(),
                IsUserDriven = true
            };

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    _logger.Info($"Registering session with backend (attempt {attempt}/{maxAttempts})");

                    var response = await _apiClient.RegisterSessionAsync(registration);

                    if (response.Success)
                    {
                        _logger.Info($"Session registered successfully: {response.SessionId}");

                        // Reconcile registry-detected enrollment type with the backend's authoritative
                        // validator verdict. Mismatches emit an enrollment_type_mismatch warning event
                        // and live-switch the flow handler on the EnrollmentTracker.
                        try
                        {
                            _collectorCoordinator?.ReconcileWithBackendValidator(response.ValidatedBy);
                        }
                        catch (Exception reconcileEx)
                        {
                            _logger.Warning($"ReconcileWithBackendValidator failed (non-fatal): {reconcileEx.Message}");
                        }

                        if (!string.IsNullOrEmpty(response.AdminAction))
                        {
                            _logger.Warning($"Session already marked as {response.AdminAction} by administrator — will run cleanup after startup");
                            _pendingAdminAction = response.AdminAction;
                        }

                        await Security.BootstrapConfigCleanup.TryDeleteIfCertReadyAsync(
                            _configuration,
                            _logger,
                            _agentVersion).ConfigureAwait(false);

                        return;
                    }

                    _logger.Warning($"Session registration failed: {response.Message}");
                }
                catch (BackendAuthException ex)
                {
                    _logger.Error($"Session registration authentication failed: {ex.Message}");
                    _uploadOrchestrator.HandleAuthFailure(ex.StatusCode);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.Error($"Failed to register session (attempt {attempt}/{maxAttempts})", ex);

                    if (attempt == maxAttempts)
                    {
                        _ = _emergencyReporter.TrySendAsync(
                            AgentErrorType.RegisterSessionFailed,
                            ex.Message);
                        return;
                    }
                }

                var delaySeconds = (int)Math.Pow(2, attempt);
                _logger.Info($"Retrying session registration in {delaySeconds}s");
                await Task.Delay(delaySeconds * 1000);
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // Lifecycle (Stop, WaitForCompletion, TriggerCleanup, Dispose)
        // ──────────────────────────────────────────────────────────────────

        public void Stop()
        {
            _logger.Info("Stopping monitoring service");

            _spool.StopWatching();
            _collectorCoordinator.StopEventCollectors();
            _collectorCoordinator.RunShutdownAnalyzers();

            EmitShutdownEvent("manual_stop", "Autopilot Monitor Agent stopped");

            _uploadOrchestrator.UploadEventsAsync().Wait(TimeSpan.FromSeconds(10));

            _logger.Info("Monitoring service stopped");
            _completionEvent.Set();
        }

        public void WaitForCompletion()
        {
            _completionEvent.Wait();
        }

        public void TriggerCleanup()
        {
            _logger.Info("TriggerCleanup invoked - executing cleanup without enrollment monitoring");

            if (_configuration.SelfDestructOnComplete)
            {
                _cleanupService.ExecuteSelfDestruct();
            }
            else
            {
                _logger.Info("SelfDestructOnComplete is disabled - nothing to clean up");
            }
        }

        public void Dispose()
        {
            _uploadOrchestrator?.Dispose();
            _collectorCoordinator?.Dispose();
            _apiClient?.Dispose();
            _spool?.Dispose();
            _remoteConfigService?.Dispose();
            _distressReporter?.Dispose();
            _completionEvent.Dispose();
        }
    }
}
