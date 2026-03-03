using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.Core.Monitoring.Tracking
{
    /// <summary>
    /// Central enrollment tracking orchestrator.
    /// Collects consolidated device info events at startup and manages ImeLogTracker
    /// for smart app installation tracking with strategic event emission.
    /// </summary>
    public class EnrollmentTracker : IDisposable
    {
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly Action<EnrollmentEvent> _emitEvent;
        private readonly AgentLogger _logger;
        private readonly string _imeLogFolder;
        private readonly List<ImeLogPattern> _imeLogPatterns;
        private readonly Collectors.EspAndHelloTracker _espAndHelloTracker;

        private ImeLogTracker _imeLogTracker;
        private Timer _summaryTimer;
        private bool _summaryTimerActive;
        private string _lastEspPhase; // Track last ESP phase to prevent duplicate events
        private bool _hasAutoSwitchedToAppsPhase; // Track if we've already auto-switched to apps phase for current ESP phase
        private string _enrollmentType = "v1"; // "v1" = Autopilot Classic/ESP, "v2" = Windows Device Preparation
        private bool _isWaitingForHello = false; // Track if we're waiting for Hello to complete before sending enrollment_complete
        private bool _finalDeviceInfoCollected = false; // Ensure final device info is emitted only once
        private string _lastEmittedSummaryHash; // Track last emitted state-breakdown to avoid redundant summary events

        // ESP failure handling (Phase 1)
        private Timer _espFailureTimer;
        private string _pendingEspFailureType;
        private const int EspFailureGracePeriodSeconds = 60;
        private static readonly HashSet<string> RecoverableEspFailureTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ESPProgress_Timeout"
        };

        // Unified completion logic (Phase 3A)
        private bool _espEverSeen;
        private bool _espFinalExitSeen;
        private bool _enrollmentCompleteEmitted;
        private bool _desktopArrived;
        private readonly DateTime _agentStartTimeUtc = DateTime.UtcNow;

        // Device-Only ESP detection (Phase 3C)
        private Timer _deviceOnlyEspTimer;
        private const int DeviceOnlyEspTimerMinutes = 5;

        // State persistence for crash recovery
        private readonly EnrollmentStatePersistence _statePersistence;
        private EnrollmentStateData _stateData = new EnrollmentStateData();
        private bool _stateDirty;

        // Completion check throttling (max 1x/min per source)
        private readonly Dictionary<string, DateTime> _lastCompletionCheckBySource = new Dictionary<string, DateTime>();

        // Default IME log folder
        private const string DefaultImeLogFolder = @"%ProgramData%\Microsoft\IntuneManagementExtension\Logs";

        private Collectors.DeviceInfoCollector _deviceInfoCollector;

        /// <summary>
        /// Detects whether the Autopilot profile indicates Hybrid Azure AD Join
        /// by reading CloudAssignedDomainJoinMethod from the AutopilotPolicyCache registry key.
        /// Safe to call before EnrollmentTracker is instantiated.
        /// Returns true if CloudAssignedDomainJoinMethod == 1.
        /// </summary>
        public static bool DetectHybridJoinStatic()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Provisioning\AutopilotPolicyCache"))
                {
                    if (key != null)
                    {
                        var domainJoinMethod = key.GetValue("CloudAssignedDomainJoinMethod")?.ToString();
                        return domainJoinMethod == "1";
                    }
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// Detects whether this is an Autopilot v1 (Classic ESP) or v2 (Windows Device Preparation) enrollment
        /// by reading the Autopilot registry keys. Safe to call before EnrollmentTracker is instantiated.
        /// Returns "v2" if WDP indicators are present, "v1" otherwise.
        /// </summary>
        public static string DetectEnrollmentTypeStatic()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Provisioning\AutopilotSettings"))
                {
                    if (key != null)
                    {
                        // CloudAssignedDeviceRegistration=2 signals WDP provisioning flow
                        var deviceReg = key.GetValue("CloudAssignedDeviceRegistration")?.ToString();
                        if (deviceReg == "2")
                            return "v2";

                        // CloudAssignedEspEnabled=0 means no ESP, characteristic of WDP
                        var espEnabled = key.GetValue("CloudAssignedEspEnabled")?.ToString();
                        if (espEnabled == "0")
                            return "v2";
                    }
                }
            }
            catch { }

            return "v1";
        }

        public EnrollmentTracker(
            string sessionId,
            string tenantId,
            Action<EnrollmentEvent> emitEvent,
            AgentLogger logger,
            List<ImeLogPattern> imeLogPatterns,
            string imeLogFolderOverride = null,
            bool simulationMode = false,
            double speedFactor = 50,
            string imeMatchLogPath = null,
            Collectors.EspAndHelloTracker espAndHelloTracker = null)
        {
            _sessionId = sessionId;
            _tenantId = tenantId;
            _emitEvent = emitEvent;
            _logger = logger;
            _imeLogPatterns = imeLogPatterns ?? new List<ImeLogPattern>();
            _imeLogFolder = imeLogFolderOverride ?? DefaultImeLogFolder;
            _espAndHelloTracker = espAndHelloTracker;
            _deviceInfoCollector = new Collectors.DeviceInfoCollector(sessionId, tenantId, emitEvent, logger);

            // State persistence for crash recovery
            var stateDirectory = @"%ProgramData%\AutopilotMonitor\State";
            _statePersistence = new EnrollmentStatePersistence(stateDirectory, _logger);

            // Create ImeLogTracker with state persistence directory
            _imeLogTracker = new ImeLogTracker(_imeLogFolder, _imeLogPatterns, _logger, matchLogPath: imeMatchLogPath, stateDirectory: stateDirectory);
            _imeLogTracker.SimulationMode = simulationMode;
            _imeLogTracker.SpeedFactor = speedFactor;

            // Wire up callbacks
            _imeLogTracker.OnEspPhaseChanged = HandleEspPhaseChanged;
            _imeLogTracker.OnImeAgentVersion = HandleImeAgentVersion;
            _imeLogTracker.OnImeStarted = HandleImeStarted;
            _imeLogTracker.OnAppStateChanged = HandleAppStateChanged;
            _imeLogTracker.OnPoliciesDiscovered = HandlePoliciesDiscovered;
            _imeLogTracker.OnAllAppsCompleted = HandleAllAppsCompleted;
            _imeLogTracker.OnUserSessionCompleted = HandleUserSessionCompleted;
            _imeLogTracker.OnDoTelemetryReceived = HandleDoTelemetryReceived;

            // Subscribe to EspAndHelloTracker completion event if available
            if (_espAndHelloTracker != null)
            {
                _espAndHelloTracker.HelloCompleted += OnHelloCompleted;
                _espAndHelloTracker.FinalizingSetupPhaseTriggered += OnFinalizingSetupPhaseTriggered;
                _espAndHelloTracker.WhiteGloveCompleted += OnWhiteGloveCompleted;
                _espAndHelloTracker.EspFailureDetected += OnEspFailureDetected;
            }
        }

        /// <summary>
        /// Starts the enrollment tracker: collects device info and starts IME log tracking.
        /// </summary>
        public void Start()
        {
            _logger.Info("EnrollmentTracker: starting");

            // Load persisted state for crash recovery
            LoadState();

            // Start IME log tracking immediately — this is the critical enrollment watcher.
            // Device info collection (WMI + registry) runs in the background so it doesn't
            // delay the ImeLogTracker from catching early enrollment events.
            _imeLogTracker.Start();

            // Collect and emit device info asynchronously (3 WMI queries + 5+ registry reads)
            Task.Run(CollectDeviceInfo);

            // TODO: Überdenken ob ein 30s timer hier wirklich immer gut ist, hab einen Fall gesehen mit 
            // laaanger Wartephase in WhiteGlove weil ein 24H2 feature update reinkam und installiert wurde

            // Start periodic summary timer (30s, starts when app tracking begins)
            _summaryTimer = new Timer(SummaryTimerCallback, null, Timeout.Infinite, Timeout.Infinite);

            _logger.Info("EnrollmentTracker: started");
        }

        /// <summary>
        /// Stops the enrollment tracker
        /// </summary>
        public void Stop()
        {
            _logger.Info("EnrollmentTracker: stopping");
            _summaryTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _imeLogTracker?.Stop();
            _logger.Info("EnrollmentTracker: stopped");
        }

        /// <summary>
        /// Updates IME log patterns (hot-reload from config change)
        /// </summary>
        public void UpdatePatterns(List<ImeLogPattern> newPatterns)
        {
            if (newPatterns != null)
            {
                _logger.Info("EnrollmentTracker: updating IME log patterns (hot-reload)");
                _imeLogTracker?.CompilePatterns(newPatterns);
            }
        }

        /// <summary>
        /// Access to the ImeLogTracker (for simulator to reference package states)
        /// </summary>
        public ImeLogTracker ImeTracker => _imeLogTracker;

        // ===== Device Info Collection =====

        private void CollectDeviceInfo()
        {
            var result = _deviceInfoCollector.CollectAll();
            _enrollmentType = result.enrollmentType;
            _stateData.EnrollmentType = _enrollmentType;
            _stateDirty = true;
        }

        /// <summary>
        /// Collects device info that may change during enrollment (e.g., BitLocker enabled via policy).
        /// Called at enrollment complete to capture final state.
        /// </summary>
        private void CollectDeviceInfoAtEnd()
            => _deviceInfoCollector.CollectAtEnd();

        // ===== ImeLogTracker Callbacks -> Strategic Events =====

        private void HandleEspPhaseChanged(string phase)
        {
            // WDP (v2) has no ESP - skip ESP phase handling entirely
            if (_enrollmentType == "v2")
            {
                _logger.Debug($"EnrollmentTracker: skipping ESP phase event in WDP enrollment (phase: {phase})");
                return;
            }

            // Only emit event if the phase has actually changed
            if (string.Equals(phase, _lastEspPhase, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Debug($"EnrollmentTracker: ESP phase unchanged ({phase}), skipping event");
                return;
            }

            _logger.Info($"EnrollmentTracker: ESP phase changed from '{_lastEspPhase ?? "null"}' to '{phase}'");
            _lastEspPhase = phase;
            _hasAutoSwitchedToAppsPhase = false; // Reset when ESP phase changes
            _espEverSeen = true;
            _stateData.EspEverSeen = true;
            _stateData.LastEspPhase = phase;
            if (_stateData.EspFirstSeenUtc == null)
                _stateData.EspFirstSeenUtc = DateTime.UtcNow;
            RecordSignal($"esp_phase_{phase}");

            // ESP phase change means ESP is progressing — cancel any pending failure grace period
            CancelPendingEspFailure();

            // Cancel device-only ESP timer if AccountSetup phase detected
            if (string.Equals(phase, "AccountSetup", StringComparison.OrdinalIgnoreCase))
            {
                if (_deviceOnlyEspTimer != null)
                {
                    _logger.Info("EnrollmentTracker: AccountSetup detected — cancelling device-only ESP timer");
                    _deviceOnlyEspTimer.Dispose();
                    _deviceOnlyEspTimer = null;
                }
            }

            // Map ESP phase to EnrollmentPhase (phase change events)
            var enrollmentPhase = EnrollmentPhase.DeviceSetup;
            if (string.Equals(phase, "AccountSetup", StringComparison.OrdinalIgnoreCase))
                enrollmentPhase = EnrollmentPhase.AccountSetup;

            _emitEvent(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = "esp_phase_changed",
                Severity = EventSeverity.Info,
                Source = "ImeLogTracker",
                Phase = enrollmentPhase,
                Message = $"ESP phase: {phase}",
                Data = new Dictionary<string, object> { { "espPhase", phase } }
            });

            // Start summary timer when we detect ESP phase
            if (!_summaryTimerActive)
            {
                _summaryTimerActive = true;
                _summaryTimer?.Change(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            }
        }

        private void HandleImeAgentVersion(string version)
        {
            _emitEvent(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = "ime_agent_version",
                Severity = EventSeverity.Info,
                Source = "ImeLogTracker",
                Phase = EnrollmentPhase.Unknown,
                Message = $"IME Agent version: {version}",
                Data = new Dictionary<string, object> { { "agentVersion", version } }
            });
        }

        private void HandleImeStarted()
        {
            _logger.Info("EnrollmentTracker: IME started event");
        }

        private void HandleAppStateChanged(AppPackageState app, AppInstallationState oldState, AppInstallationState newState)
        {
            // Auto-switch to app installation phase when first app activity detected
            // If we're in DeviceSetup and an app starts downloading/installing, switch to AppsDevice
            // If we're in AccountSetup and an app starts downloading/installing, switch to AppsUser
            if (!_hasAutoSwitchedToAppsPhase &&
                (newState == AppInstallationState.Downloading || newState == AppInstallationState.Installing) &&
                oldState < AppInstallationState.Downloading)
            {
                if (_lastEspPhase != null)
                {
                    if (string.Equals(_lastEspPhase, "DeviceSetup", StringComparison.OrdinalIgnoreCase))
                    {
                        // Switch from DeviceSetup to AppsDevice
                        _logger.Info($"EnrollmentTracker: First app activity detected during DeviceSetup, switching to AppsDevice phase");
                        _hasAutoSwitchedToAppsPhase = true;
                        _emitEvent(new EnrollmentEvent
                        {
                            SessionId = _sessionId,
                            TenantId = _tenantId,
                            EventType = "esp_phase_changed",
                            Severity = EventSeverity.Info,
                            Source = "EnrollmentTracker",
                            Phase = EnrollmentPhase.AppsDevice,
                            Message = "ESP phase: AppsDevice (auto-detected from app activity)",
                            Data = new Dictionary<string, object> { { "espPhase", "AppsDevice" }, { "autoDetected", true } }
                        });
                    }
                    else if (string.Equals(_lastEspPhase, "AccountSetup", StringComparison.OrdinalIgnoreCase))
                    {
                        // Switch from AccountSetup to AppsUser
                        _logger.Info($"EnrollmentTracker: First app activity detected during AccountSetup, switching to AppsUser phase");
                        _hasAutoSwitchedToAppsPhase = true;
                        _emitEvent(new EnrollmentEvent
                        {
                            SessionId = _sessionId,
                            TenantId = _tenantId,
                            EventType = "esp_phase_changed",
                            Severity = EventSeverity.Info,
                            Source = "EnrollmentTracker",
                            Phase = EnrollmentPhase.AppsUser,
                            Message = "ESP phase: AppsUser (auto-detected from app activity)",
                            Data = new Dictionary<string, object> { { "espPhase", "AppsUser" }, { "autoDetected", true } }
                        });
                    }
                }
            }

            // Only emit strategic events for significant state transitions
            string eventType;
            var severity = EventSeverity.Info;
            var phase = EnrollmentPhase.Unknown; // Apps set to Unknown, will be sorted chronologically into active phase

            switch (newState)
            {
                case AppInstallationState.Downloading:
                    // Emit strategic event once when download starts
                    if (oldState < AppInstallationState.Downloading)
                    {
                        eventType = "app_download_started";
                    }
                    else
                    {
                        // Emit debug event for download progress updates
                        // Skip if no real download data (bytesTotal too small or zero)
                        if (app.BytesTotal > 1024) // At least 1 KB to be a real download
                        {
                            _emitEvent(new EnrollmentEvent
                            {
                                SessionId = _sessionId,
                                TenantId = _tenantId,
                                EventType = "download_progress",
                                Severity = EventSeverity.Debug,
                                Source = "ImeLogTracker",
                                Phase = phase,
                                Message = $"{app.Name ?? app.Id}: {app.ProgressPercent}%",
                                Data = app.ToEventData()
                            });
                        }
                        return; // Skip main event emission below
                    }
                    break;

                case AppInstallationState.Installing:
                    // Only emit the strategic event once when install actually starts.
                    // Progress-only updates (oldState == Installing, just progress/bytes changed)
                    // are skipped — same pattern as Downloading above.
                    if (oldState == AppInstallationState.Installing)
                        return;
                    eventType = "app_install_started";
                    break;

                case AppInstallationState.Installed:
                    eventType = "app_install_completed";
                    // Emit download_progress event for download manager (shows as completed)
                    _emitEvent(new EnrollmentEvent
                    {
                        SessionId = _sessionId,
                        TenantId = _tenantId,
                        EventType = "download_progress",
                        Severity = EventSeverity.Debug,
                        Source = "ImeLogTracker",
                        Phase = phase,
                        Message = $"{app.Name ?? app.Id}: completed",
                        Data = new Dictionary<string, object>(app.ToEventData())
                        {
                            ["status"] = "completed"
                        }
                    });
                    break;

                case AppInstallationState.Skipped:
                    eventType = "app_install_skipped";
                    break;

                case AppInstallationState.Error:
                    eventType = "app_install_failed";
                    severity = EventSeverity.Error;
                    // Emit download_progress event for download manager (shows as failed)
                    _emitEvent(new EnrollmentEvent
                    {
                        SessionId = _sessionId,
                        TenantId = _tenantId,
                        EventType = "download_progress",
                        Severity = EventSeverity.Debug,
                        Source = "ImeLogTracker",
                        Phase = phase,
                        Message = $"{app.Name ?? app.Id}: failed",
                        Data = new Dictionary<string, object>(app.ToEventData())
                        {
                            ["status"] = "failed"
                        }
                    });
                    break;

                case AppInstallationState.Postponed:
                    eventType = "app_install_failed";
                    severity = EventSeverity.Warning;
                    break;

                default:
                    return; // Don't emit for Unknown, NotInstalled, InProgress
            }

            // Build a descriptive message: include error detail if available
            var message = $"{app.Name ?? app.Id}: {newState}";
            if (newState == AppInstallationState.Error && !string.IsNullOrEmpty(app.ErrorDetail))
                message = $"{app.Name ?? app.Id}: {app.ErrorDetail}";

            _emitEvent(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = eventType,
                Severity = severity,
                Source = "ImeLogTracker",
                Phase = phase,
                Message = message,
                Data = app.ToEventData()
            });

            // Emit summary immediately if state-breakdown counters changed (instant UI updates)
            EmitAppTrackingSummaryIfChanged();
        }

        private void HandlePoliciesDiscovered(string policiesJson)
        {
            _logger.Info($"EnrollmentTracker: policies discovered, tracking {_imeLogTracker.PackageStates.Count} apps");
        }

        private void HandleAllAppsCompleted()
        {
            _logger.Info("EnrollmentTracker: all apps completed");

            // Stop summary timer
            _summaryTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _summaryTimerActive = false;

            // Emit final summary
            EmitAppTrackingSummary();

            // Note: Phase transition to FinalizingSetup is now handled by Shell-Core events
            // (ESP exit or Hello wizard start) for more robust detection.
            // We no longer automatically transition here when apps complete.
            if (_lastEspPhase != null)
            {
                _logger.Info($"EnrollmentTracker: All apps completed while in phase '{_lastEspPhase}'");
                _logger.Info("EnrollmentTracker: Waiting for ESP exit or Hello wizard events to transition to FinalizingSetup");
            }
        }

        private void HandleDoTelemetryReceived(AppPackageState app)
        {
            _logger.Info($"EnrollmentTracker: DO telemetry received for {app.Name ?? app.Id}");

            var phase = EnrollmentPhase.Unknown;

            // Dedicated do_telemetry event for backend aggregation
            _emitEvent(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = "do_telemetry",
                Severity = EventSeverity.Info,
                Source = "ImeLogTracker",
                Phase = phase,
                Message = $"{app.Name ?? app.Id}: DO complete - {app.DoPercentPeerCaching}% peers, mode={app.DoDownloadMode}",
                Data = app.ToEventData()
            });

            // Also emit download_progress so the UI picks up DO stats
            _emitEvent(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = "download_progress",
                Severity = EventSeverity.Debug,
                Source = "ImeLogTracker",
                Phase = phase,
                Message = $"{app.Name ?? app.Id}: DO telemetry received",
                Data = app.ToEventData()
            });
        }

        private void HandleUserSessionCompleted()
        {
            _logger.Info("EnrollmentTracker: User session completed (detected from IME log)");
            _stateData.ImePatternSeenUtc = DateTime.UtcNow;
            RecordSignal("ime_pattern");

            // User session completed successfully — cancel any pending ESP failure
            CancelPendingEspFailure();

            // Stop summary timer if running
            _summaryTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _summaryTimerActive = false;

            // Check if Windows Hello is configured but not yet completed
            if (_espAndHelloTracker != null)
            {
                bool helloPolicyConfigured = _espAndHelloTracker.IsPolicyConfigured;
                bool helloCompleted = _espAndHelloTracker.IsHelloCompleted;

                if (helloPolicyConfigured && !helloCompleted)
                {
                    // Hello is configured but not finished yet - DO NOT mark enrollment as complete
                    _logger.Info("EnrollmentTracker: Windows Hello policy is configured but provisioning has not completed yet.");
                    _logger.Info("EnrollmentTracker: Waiting for Hello provisioning to finish before marking enrollment as complete.");

                    _emitEvent(new EnrollmentEvent
                    {
                        SessionId = _sessionId,
                        TenantId = _tenantId,
                        EventType = "waiting_for_hello",
                        Severity = EventSeverity.Info,
                        Source = "EnrollmentTracker",
                        Phase = EnrollmentPhase.FinalizingSetup,
                        Message = "User apps completed - waiting for Windows Hello provisioning to finish"
                    });

                    // Set flag so we know we're waiting
                    _isWaitingForHello = true;

                    // Note: enrollment_complete will be triggered when Hello events arrive
                    // or when the agent is stopped/times out
                    return;
                }
            }

            TryEmitEnrollmentComplete("ime_pattern");
        }

        /// <summary>
        /// Called when Windows Hello provisioning completes (via EspAndHelloTracker event).
        /// Multiple completion paths: IME pattern was waiting for Hello, ESP final exit + Hello composite,
        /// or Desktop arrival + Hello.
        /// </summary>
        private void OnHelloCompleted(object sender, EventArgs e)
        {
            _logger.Info("EnrollmentTracker: Received HelloCompleted event from EspAndHelloTracker");
            _stateData.HelloResolvedUtc = DateTime.UtcNow;
            RecordSignal("hello_resolved");

            if (_isWaitingForHello)
            {
                _logger.Info("EnrollmentTracker: Hello provisioning completed while waiting (IME path) - marking enrollment as complete now");
                _isWaitingForHello = false;
                TryEmitEnrollmentComplete("ime_hello");
            }
            else if (_espFinalExitSeen)
            {
                _logger.Info("EnrollmentTracker: Hello completed + ESP final exit seen — composite completion");
                TryEmitEnrollmentComplete("esp_hello_composite");
            }
            else if (_desktopArrived)
            {
                _logger.Info("EnrollmentTracker: Hello completed + desktop arrived — desktop-hello completion");
                TryEmitEnrollmentComplete("desktop_hello");
            }
            else
            {
                _logger.Debug("EnrollmentTracker: HelloCompleted event received but no completion trigger active yet");
            }
        }

        /// <summary>
        /// Called when WhiteGlove (Pre-Provisioning) completes successfully.
        /// Emits the whiteglove_complete event. The MonitoringService handles
        /// the actual agent shutdown upon seeing this event type.
        /// </summary>
        private void OnWhiteGloveCompleted(object sender, EventArgs e)
        {
            _logger.Info("EnrollmentTracker: WhiteGlove pre-provisioning completed");

            // Stop summary timer — no more app tracking needed
            _summaryTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            _summaryTimerActive = false;

            _emitEvent(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = "whiteglove_complete",
                Severity = EventSeverity.Info,
                Source = "EnrollmentTracker",
                Phase = EnrollmentPhase.Complete,
                Message = "WhiteGlove (Pre-Provisioning) completed \u2014 device entering pending state"
            });

            // WICHTIG: Kein WriteEnrollmentCompleteMarker()!
            // Das Marker-File wuerde verhindern, dass der Agent beim naechsten Start wieder laeuft.
            // WICHTIG: Kein _imeLogTracker?.DeleteState()!
            // Der State wird fuer Part 2 benoetigt falls der Tracker weiterlaufen muss.
        }

        /// <summary>
        /// Called when an ESP failure is detected (ESPProgress_Failure, _Timeout, _Abort, WhiteGlove_Failed, etc.).
        /// Terminal failures emit enrollment_failed immediately.
        /// Recoverable failures (e.g. ESPProgress_Timeout) get a grace period before failure.
        /// </summary>
        private void OnEspFailureDetected(object sender, string failureType)
        {
            _logger.Info($"EnrollmentTracker: ESP failure detected: {failureType}");

            if (RecoverableEspFailureTypes.Contains(failureType))
            {
                // Recoverable failure — start grace period timer
                _logger.Info($"EnrollmentTracker: '{failureType}' is recoverable — starting {EspFailureGracePeriodSeconds}s grace period");
                _pendingEspFailureType = failureType;

                // Cancel existing timer if any (e.g. second timeout event)
                _espFailureTimer?.Dispose();
                _espFailureTimer = new Timer(
                    OnEspFailureGracePeriodExpired,
                    null,
                    TimeSpan.FromSeconds(EspFailureGracePeriodSeconds),
                    TimeSpan.FromMilliseconds(-1));
            }
            else
            {
                // Terminal failure — emit enrollment_failed immediately
                _logger.Info($"EnrollmentTracker: '{failureType}' is terminal — emitting enrollment_failed immediately");
                EmitEnrollmentFailed(failureType, "esp_failure");
            }
        }

        /// <summary>
        /// Called when the ESP failure grace period expires without recovery.
        /// Emits enrollment_failed.
        /// </summary>
        private void OnEspFailureGracePeriodExpired(object state)
        {
            var failureType = _pendingEspFailureType ?? "unknown";
            _logger.Warning($"EnrollmentTracker: ESP failure grace period ({EspFailureGracePeriodSeconds}s) expired for '{failureType}' — emitting enrollment_failed");
            _pendingEspFailureType = null;

            EmitEnrollmentFailed(failureType, "esp_failure_grace_expired");
        }

        /// <summary>
        /// Cancels any pending ESP failure grace period timer (called when recovery is detected).
        /// </summary>
        private void CancelPendingEspFailure()
        {
            if (_espFailureTimer != null)
            {
                _logger.Info($"EnrollmentTracker: ESP recovery detected — cancelling pending failure for '{_pendingEspFailureType}'");
                _espFailureTimer.Dispose();
                _espFailureTimer = null;
                _pendingEspFailureType = null;
            }
        }

        /// <summary>
        /// Emits an enrollment_failed event. The MonitoringService handles shutdown identically to enrollment_complete.
        /// </summary>
        private void EmitEnrollmentFailed(string failureType, string failureSource)
        {
            // Stop summary timer
            _summaryTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _summaryTimerActive = false;

            _emitEvent(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = "enrollment_failed",
                Severity = EventSeverity.Error,
                Source = "EnrollmentTracker",
                Phase = EnrollmentPhase.Complete,
                Message = $"Autopilot enrollment failed: {failureType}",
                Data = new Dictionary<string, object>
                {
                    { "failureType", failureType },
                    { "failureSource", failureSource },
                    { "signalsSeen", _stateData.SignalsSeen },
                    { "agentUptimeSeconds", (DateTime.UtcNow - _agentStartTimeUtc).TotalSeconds }
                }
            });

            // Clean up persisted tracker state
            _imeLogTracker?.DeleteState();
            _statePersistence.Delete();
        }

        /// <summary>
        /// Called when ESP exit or Hello wizard start is detected (via HelloDetector Shell-Core events)
        /// Triggers transition to FinalizingSetup phase
        /// </summary>
        private void OnFinalizingSetupPhaseTriggered(object sender, string reason)
        {
            _logger.Info($"EnrollmentTracker: FinalizingSetup phase trigger received - reason: {reason}");

            // If ESP exiting, check which phase we're in
            if (reason == "esp_exiting")
            {
                _espEverSeen = true;

                if (string.Equals(_lastEspPhase, "AccountSetup", StringComparison.OrdinalIgnoreCase) || _desktopArrived)
                {
                    // Final ESP exit: either AccountSetup phase detected OR desktop arrived (backup)
                    var phaseInfo = string.Equals(_lastEspPhase, "AccountSetup", StringComparison.OrdinalIgnoreCase)
                        ? "AccountSetup"
                        : $"{_lastEspPhase ?? "unknown"} (desktop arrival backup)";
                    _logger.Info($"EnrollmentTracker: ESP final exit from {phaseInfo} — marking _espFinalExitSeen, starting Hello wait timer");

                    _espFinalExitSeen = true;
                    _stateData.EspFinalExitSeen = true;
                    _stateData.EspFinalExitUtc = DateTime.UtcNow;
                    RecordSignal("esp_final_exit");

                    // Emit phase change event to FinalizingSetup
                    _emitEvent(new EnrollmentEvent
                    {
                        SessionId = _sessionId,
                        TenantId = _tenantId,
                        EventType = "esp_phase_changed",
                        Severity = EventSeverity.Info,
                        Source = "EnrollmentTracker",
                        Phase = EnrollmentPhase.FinalizingSetup,
                        Message = $"ESP phase: FinalizingSetup ({phaseInfo} completed, waiting for final steps)",
                        Data = new Dictionary<string, object>
                        {
                            { "espPhase", "FinalizingSetup" },
                            { "autoDetected", true },
                            { "triggeredBy", reason },
                            { "previousPhase", _lastEspPhase ?? "unknown" },
                            { "desktopArrivedBackup", _desktopArrived && !string.Equals(_lastEspPhase, "AccountSetup", StringComparison.OrdinalIgnoreCase) }
                        }
                    });

                    CollectDeviceInfoAtFinalizingSetup(reason);

                    // Start Hello wait timer (waits for Hello wizard to start or timeout)
                    _espAndHelloTracker?.StartHelloWaitTimer();

                    // If Hello was already resolved (e.g., via EventLog backfill or Event 300/301
                    // during AccountSetup), the composite signal can fire immediately.
                    TryEmitEnrollmentComplete("esp_hello_composite");
                }
                else
                {
                    // DeviceSetup phase exiting without desktop — intermediate transition (Device→Account) or device-only ESP
                    _logger.Info($"EnrollmentTracker: ESP phase exiting from '{_lastEspPhase ?? "unknown"}' - intermediate transition, starting device-only ESP detection timer ({DeviceOnlyEspTimerMinutes}min)");

                    // Start device-only ESP detection timer
                    _deviceOnlyEspTimer?.Dispose();
                    _deviceOnlyEspTimer = new Timer(
                        OnDeviceOnlyEspTimerExpired,
                        null,
                        TimeSpan.FromMinutes(DeviceOnlyEspTimerMinutes),
                        TimeSpan.FromMilliseconds(-1));
                }
            }
            else if (reason == "hello_wizard_started")
            {
                // Hello wizard started - transition to FinalizingSetup regardless of previous phase
                _logger.Info("EnrollmentTracker: Hello wizard started - transitioning to FinalizingSetup phase");

                _emitEvent(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId = _tenantId,
                    EventType = "esp_phase_changed",
                    Severity = EventSeverity.Info,
                    Source = "EnrollmentTracker",
                    Phase = EnrollmentPhase.FinalizingSetup,
                    Message = "ESP phase: FinalizingSetup (Hello wizard started)",
                    Data = new Dictionary<string, object>
                    {
                        { "espPhase", "FinalizingSetup" },
                        { "autoDetected", true },
                        { "triggeredBy", reason }
                    }
                });

                CollectDeviceInfoAtFinalizingSetup(reason);
            }
        }

        // ===== State Persistence =====

        private void LoadState()
        {
            var loaded = _statePersistence.Load();
            if (loaded == null)
                return;

            _espEverSeen = loaded.EspEverSeen;
            _espFinalExitSeen = loaded.EspFinalExitSeen;
            _desktopArrived = loaded.DesktopArrived;
            _lastEspPhase = loaded.LastEspPhase;
            _isWaitingForHello = loaded.IsWaitingForHello;
            _enrollmentCompleteEmitted = loaded.EnrollmentCompleteEmitted;
            _enrollmentType = loaded.EnrollmentType ?? _enrollmentType;
            _stateData = loaded;

            _logger.Info($"EnrollmentTracker: state restored — espEverSeen={_espEverSeen}, espFinalExitSeen={_espFinalExitSeen}, desktopArrived={_desktopArrived}, lastEspPhase={_lastEspPhase}, enrollmentCompleteEmitted={_enrollmentCompleteEmitted}");

            // Restart Hello wait timer if needed after crash recovery
            if ((_desktopArrived || _espFinalExitSeen) && _espAndHelloTracker != null && !_espAndHelloTracker.IsHelloCompleted)
            {
                _logger.Info("EnrollmentTracker: restarting Hello wait timer after state recovery");
                _espAndHelloTracker.StartHelloWaitTimer();
            }
        }

        private void RecordSignal(string signal)
        {
            if (!_stateData.SignalsSeen.Contains(signal))
                _stateData.SignalsSeen.Add(signal);
            _stateDirty = true;
        }

        // ===== Unified Completion Logic =====

        /// <summary>
        /// Central guard method for enrollment_complete emission. All completion paths route through here.
        /// An _enrollmentCompleteEmitted flag prevents double emission.
        /// Emits a throttled completion_check event on every call for observability.
        /// </summary>
        private void TryEmitEnrollmentComplete(string source)
        {
            if (_enrollmentCompleteEmitted)
            {
                _logger.Debug($"EnrollmentTracker: TryEmitEnrollmentComplete('{source}') — already emitted, skipping");
                return;
            }

            if (string.IsNullOrEmpty(source))
                return;

            // Hello-Check: Hello must be resolved before we can complete
            bool helloResolved = _espAndHelloTracker == null
                || _espAndHelloTracker.IsHelloCompleted
                || !_espAndHelloTracker.IsPolicyConfigured;

            if (!helloResolved)
            {
                _logger.Info($"EnrollmentTracker: Completion source '{source}' ready but Hello still pending — not completing yet");
                EmitCompletionCheck(source, "hello_pending", "hello_not_resolved");
                return;
            }

            // Desktop-Arrival Gate: Block desktop-based completion when ESP is still actively running.
            // Both "desktop_arrival" (direct) and "desktop_hello" (Hello resolved after desktop arrival)
            // must be gated — otherwise Hello timeout during active AccountSetup triggers premature completion.
            // WDP v2 has no ESP — skip the gate entirely.
            if ((source == "desktop_arrival" || source == "desktop_hello") && _enrollmentType != "v2" && _espEverSeen && !_espFinalExitSeen)
            {
                _logger.Info($"EnrollmentTracker: Completion source '{source}' blocked — ESP still active");
                EmitCompletionCheck(source, "blocked", "esp_active");
                return;
            }

            _enrollmentCompleteEmitted = true;
            _stateData.EnrollmentCompleteEmitted = true;

            // Stop summary timer
            _summaryTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _summaryTimerActive = false;

            var helloOutcome = _espAndHelloTracker?.HelloOutcome ?? "unknown";

            var signalTimestamps = new Dictionary<string, string>();
            if (_stateData.EspFirstSeenUtc.HasValue)
                signalTimestamps["espFirstSeen"] = _stateData.EspFirstSeenUtc.Value.ToString("o");
            if (_stateData.DesktopArrivedUtc.HasValue)
                signalTimestamps["desktopArrived"] = _stateData.DesktopArrivedUtc.Value.ToString("o");
            if (_stateData.EspFinalExitUtc.HasValue)
                signalTimestamps["espFinalExit"] = _stateData.EspFinalExitUtc.Value.ToString("o");
            if (_stateData.HelloResolvedUtc.HasValue)
                signalTimestamps["helloResolved"] = _stateData.HelloResolvedUtc.Value.ToString("o");
            if (_stateData.ImePatternSeenUtc.HasValue)
                signalTimestamps["imePatternSeen"] = _stateData.ImePatternSeenUtc.Value.ToString("o");

            _emitEvent(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = "enrollment_complete",
                Severity = EventSeverity.Info,
                Source = "EnrollmentTracker",
                Phase = EnrollmentPhase.Complete,
                Message = $"Autopilot enrollment completed successfully (source: {source})",
                Data = new Dictionary<string, object>
                {
                    { "completionSource", source },
                    { "helloOutcome", helloOutcome },
                    { "signalsSeen", _stateData.SignalsSeen },
                    { "signalTimestamps", signalTimestamps },
                    { "agentUptimeSeconds", (DateTime.UtcNow - _agentStartTimeUtc).TotalSeconds }
                }
            });

            // Write enrollment complete marker for cleanup retry detection
            WriteEnrollmentCompleteMarker();

            // Clean up persisted tracker state so next enrollment starts fresh
            _imeLogTracker?.DeleteState();
            _statePersistence.Delete();
        }

        /// <summary>
        /// Emits a throttled completion_check event for observability.
        /// Max 1 event per minute per source to avoid flooding.
        /// </summary>
        private void EmitCompletionCheck(string source, string result, string reason)
        {
            // Throttle: max 1x per minute per source
            var now = DateTime.UtcNow;
            if (_lastCompletionCheckBySource.TryGetValue(source, out var lastEmit) && (now - lastEmit).TotalSeconds < 60)
                return;
            _lastCompletionCheckBySource[source] = now;

            _emitEvent(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = "completion_check",
                Severity = EventSeverity.Info,
                Source = "EnrollmentTracker",
                Phase = EnrollmentPhase.Unknown,
                Message = $"Completion source '{source}' evaluated — {result}",
                Data = new Dictionary<string, object>
                {
                    { "source", source },
                    { "result", result },
                    { "reason", reason },
                    { "espEverSeen", _espEverSeen },
                    { "espFinalExitSeen", _espFinalExitSeen },
                    { "desktopArrived", _desktopArrived },
                    { "helloResolved", _espAndHelloTracker?.IsHelloCompleted ?? false },
                    { "helloPolicyConfigured", _espAndHelloTracker?.IsPolicyConfigured ?? false },
                    { "enrollmentType", _enrollmentType },
                    { "lastEspPhase", _lastEspPhase ?? "none" }
                }
            });
        }

        /// <summary>
        /// Called by MonitoringService when Desktop Arrival is detected (explorer.exe under a real user).
        /// Corrects phase if needed, starts Hello wait timer in no-ESP scenarios, and attempts completion.
        /// </summary>
        public void NotifyDesktopArrived()
        {
            if (_desktopArrived)
                return;

            _desktopArrived = true;
            _stateData.DesktopArrived = true;
            _stateData.DesktopArrivedUtc = DateTime.UtcNow;
            RecordSignal("desktop_arrived");
            _logger.Info("EnrollmentTracker: Desktop arrival notified");

            // Phase correction: If ESP was seen but AccountSetup was never detected by IME log,
            // correct the phase and emit an event for the timeline
            if (_espEverSeen && !string.Equals(_lastEspPhase, "AccountSetup", StringComparison.OrdinalIgnoreCase))
            {
                var previousPhase = _lastEspPhase ?? "unknown";
                _lastEspPhase = "AccountSetup";
                _logger.Info($"EnrollmentTracker: Desktop arrival confirmed AccountSetup phase (was: {previousPhase}) — phase corrected on timeline");

                _emitEvent(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId = _tenantId,
                    EventType = "esp_phase_changed",
                    Severity = EventSeverity.Info,
                    Source = "DesktopArrivalDetector",
                    Phase = EnrollmentPhase.AccountSetup,
                    Message = $"ESP phase: AccountSetup (auto-detected from desktop arrival, was: {previousPhase})",
                    Data = new Dictionary<string, object>
                    {
                        { "espPhase", "AccountSetup" },
                        { "autoDetected", true },
                        { "correctedBy", "desktop_arrival" },
                        { "previousPhase", previousPhase }
                    }
                });
            }

            // Start Hello wait timer ONLY when ESP is NOT actively running.
            // During active ESP (AccountSetup runs in background with desktop visible),
            // the Hello timer must wait until ESP exits (started in OnFinalizingSetupPhaseTriggered).
            // Without this guard, Hello timeout-resolves while ESP still installs apps → premature completion.
            if (_espAndHelloTracker != null && !_espAndHelloTracker.IsHelloCompleted
                && (!_espEverSeen || _espFinalExitSeen))
            {
                _logger.Info("EnrollmentTracker: Desktop arrived with Hello pending (no active ESP) — starting Hello wait timer");
                _espAndHelloTracker.StartHelloWaitTimer();
            }

            TryEmitEnrollmentComplete("desktop_arrival");
        }

        /// <summary>
        /// Called when the device-only ESP timer expires. If no AccountSetup phase started
        /// and desktop is available, classify as device-only ESP and mark final exit.
        /// </summary>
        private void OnDeviceOnlyEspTimerExpired(object state)
        {
            // AccountSetup started meanwhile? Timer is obsolete
            if (string.Equals(_lastEspPhase, "AccountSetup", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Debug("EnrollmentTracker: Device-only ESP timer expired but AccountSetup detected — ignoring");
                return;
            }

            if (_desktopArrived)
            {
                _logger.Info($"EnrollmentTracker: No AccountSetup phase after {DeviceOnlyEspTimerMinutes}min — classified as device-only ESP, desktop is active");
                _espFinalExitSeen = true;
                _stateData.EspFinalExitSeen = true;
                _stateData.EspFinalExitUtc = DateTime.UtcNow;
                RecordSignal("device_only_esp_final_exit");
                _espAndHelloTracker?.StartHelloWaitTimer();
            }
            else
            {
                _logger.Info($"EnrollmentTracker: No AccountSetup phase after {DeviceOnlyEspTimerMinutes}min and no desktop yet — waiting for Desktop Arrival or Lifetime Timer");
            }
        }

        private void CollectDeviceInfoAtFinalizingSetup(string triggerReason)
        {
            if (_finalDeviceInfoCollected)
            {
                _logger.Debug($"EnrollmentTracker: final device info already collected, skipping (trigger: {triggerReason})");
                return;
            }

            _finalDeviceInfoCollected = true;
            _logger.Info($"EnrollmentTracker: triggering final device info collection at FinalizingSetup (trigger: {triggerReason})");
            CollectDeviceInfoAtEnd();
        }

        private void SummaryTimerCallback(object state)
        {
            // Only emit if state-breakdown counters changed since last emission (backstop for missed events)
            EmitAppTrackingSummaryIfChanged();

            // Periodic state save (piggybacks on the existing 30s timer)
            if (_stateDirty)
            {
                _stateDirty = false;
                _statePersistence.Save(_stateData);
            }
        }

        /// <summary>
        /// Emits app_tracking_summary only if state-breakdown counters changed since last emission.
        /// Called both event-driven (from HandleAppStateChanged) and periodically (30s timer backstop).
        /// </summary>
        private void EmitAppTrackingSummaryIfChanged()
        {
            var states = _imeLogTracker?.PackageStates;
            if (states == null || states.CountAll == 0) return;

            var hash = GetSummaryHash(states);
            if (hash == _lastEmittedSummaryHash) return;

            _lastEmittedSummaryHash = hash;

            _emitEvent(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = "app_tracking_summary",
                Severity = states.HasError ? EventSeverity.Warning : EventSeverity.Info,
                Source = "EnrollmentTracker",
                Phase = EnrollmentPhase.Unknown,
                Message = $"App tracking: {states.CountCompleted}/{states.CountAll} completed" +
                          (states.HasError ? $" ({states.ErrorCount} errors)" : ""),
                Data = states.GetSummaryData()
            });
        }

        /// <summary>
        /// Emits app_tracking_summary unconditionally (for final summary on completion).
        /// </summary>
        private void EmitAppTrackingSummary()
        {
            var states = _imeLogTracker?.PackageStates;
            if (states == null || states.CountAll == 0) return;

            _lastEmittedSummaryHash = GetSummaryHash(states);

            _emitEvent(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = "app_tracking_summary",
                Severity = states.HasError ? EventSeverity.Warning : EventSeverity.Info,
                Source = "EnrollmentTracker",
                Phase = EnrollmentPhase.Unknown,
                Message = $"App tracking: {states.CountCompleted}/{states.CountAll} completed" +
                          (states.HasError ? $" ({states.ErrorCount} errors)" : ""),
                Data = states.GetSummaryData()
            });
        }

        private static string GetSummaryHash(AppPackageStateList states)
        {
            return $"{states.CountAll}_{states.CountCompleted}_{states.ErrorCount}_" +
                   $"{states.Count(x => x.InstallationState == AppInstallationState.Downloading)}_" +
                   $"{states.Count(x => x.InstallationState == AppInstallationState.Installing)}_" +
                   $"{states.Count(x => x.InstallationState == AppInstallationState.Installed)}";
        }

        public void Dispose()
        {
            Stop();

            // Unsubscribe from EspAndHelloTracker events
            if (_espAndHelloTracker != null)
            {
                _espAndHelloTracker.HelloCompleted -= OnHelloCompleted;
                _espAndHelloTracker.FinalizingSetupPhaseTriggered -= OnFinalizingSetupPhaseTriggered;
                _espAndHelloTracker.WhiteGloveCompleted -= OnWhiteGloveCompleted;
                _espAndHelloTracker.EspFailureDetected -= OnEspFailureDetected;
            }

            _espFailureTimer?.Dispose();
            _deviceOnlyEspTimer?.Dispose();
            _summaryTimer?.Dispose();
            _imeLogTracker?.Dispose();
        }

        /// <summary>
        /// Writes an enrollment complete marker to the state directory.
        /// This marker is checked on agent restart to handle cleanup retry if scheduled task fails.
        /// </summary>
        private void WriteEnrollmentCompleteMarker()
        {
            try
            {
                var stateDirectory = Environment.ExpandEnvironmentVariables(@"%ProgramData%\AutopilotMonitor\State");
                Directory.CreateDirectory(stateDirectory);

                var markerPath = Path.Combine(stateDirectory, "enrollment-complete.marker");
                File.WriteAllText(markerPath, $"Enrollment completed at {DateTime.UtcNow:O}");

                _logger.Info($"Enrollment complete marker written: {markerPath}");
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to write enrollment complete marker: {ex.Message}");
            }
        }
    }
}
