using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Flows;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Completion;

namespace AutopilotMonitor.Agent.Core.Monitoring.Enrollment
{
    /// <summary>
    /// Central enrollment tracking orchestrator.
    /// Collects consolidated device info events at startup and manages ImeLogTracker
    /// for smart app installation tracking with strategic event emission.
    /// </summary>
    public partial class EnrollmentTracker : IDisposable
    {
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly Action<EnrollmentEvent> _emitEvent;
        private readonly AgentLogger _logger;
        private readonly string _imeLogFolder;
        private readonly List<ImeLogPattern> _imeLogPatterns;
        private readonly SystemSignals.EspAndHelloTracker _espAndHelloTracker;

        private ImeLogTracker _imeLogTracker;
        private Timer _summaryTimer;
        private bool _summaryTimerActive;
        private Timer _debugStateTimer;
        private bool _debugStateTimerActive;

        // ===== Thread-safety =====
        // All mutable state fields below are accessed from multiple threads:
        //   - Timer callbacks (SummaryTimer, DebugStateTimer, safety timers) → ThreadPool
        //   - ImeLogTracker event handlers (HandleEspPhaseChanged, HandleAppStateChanged, etc.) → FileSystemWatcher thread
        //   - EspAndHelloTracker event handlers (OnHelloCompleted, OnFinalizingSetupPhaseTriggered, etc.) → COM thread
        //   - MonitoringService (NotifyDesktopArrived) → desktop detection thread
        //   - Task.Run (CollectDeviceInfo) → ThreadPool
        //
        // A single lock protects all shared fields. Lock sections contain ONLY field reads/writes —
        // never _emitEvent, _logger, timer operations, or external callbacks. This ensures:
        //   - No deadlocks (no nested locking, no I/O under lock)
        //   - Minimal contention (microsecond-level critical sections)
        //   - Consistent multi-field reads (e.g., TryEmitEnrollmentComplete guard checks)
        private readonly object _stateLock = new object();

        private string _lastEspPhase; // Track last ESP phase to prevent duplicate events
        private bool _hasAutoSwitchedToAppsPhase; // Track if we've already auto-switched to apps phase for current ESP phase
        private string _enrollmentType = "v1"; // "v1" = Autopilot Classic/ESP, "v2" = Windows Device Preparation
        private IEnrollmentFlowHandler _flowHandler = EnrollmentFlowFactory.FromWireFormat("v1"); // Kept in sync with _enrollmentType via SetEnrollmentType()
        private bool _isWaitingForHello = false; // Track if we're waiting for Hello to complete before sending enrollment_complete
        private readonly bool _isBootstrapMode; // Agent started via bootstrap token (pre-MDM)
        private bool _sendTraceEvents = true; // Send Trace-severity events to backend for decision auditing

        // TEMPORARY: shadow SM rollout verification — remove when CompletionStateMachine is promoted to primary
        private string _lastShadowDiscrepancySignature;
        private bool _enrollmentStartDeviceInfoCollected = false; // Re-collect enrollment-dependent info once at first ESP phase
        private bool _finalDeviceInfoCollected = false; // Ensure final device info is emitted only once
        private string _lastEmittedSummaryHash; // Track last emitted state-breakdown to avoid redundant summary events
        private string _lastEmittedSummaryCompactHash; // Track completed+error counts only — changes here elevate severity to Info

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
        private bool _isHybridJoin;
        private readonly DateTime _agentStartTimeUtc = DateTime.UtcNow;

        // Device info readiness — guards against race where completion signals arrive before CollectDeviceInfo finishes
        private volatile bool _deviceInfoCollected;
        private string _pendingCompletionSource;

        // Device-Only ESP detection (Phase 3C)
        private Timer _deviceOnlyEspTimer;
        private const int DeviceOnlyEspTimerMinutes = 5;

        // ESP configuration from registry (FirstSync\SkipUserStatusPage / SkipDeviceStatusPage)
        // null = unknown (registry keys not present), true = skip, false = show
        private bool? _skipUserStatusPage;
        private bool? _skipDeviceStatusPage;

        // Autopilot deployment mode: 0=UserDriven, 1/2/3+=not yet confirmed mapping, null=unknown
        private int? _autopilotMode;
        private bool IsSelfDeploying => _autopilotMode == 1;

        // True when AAD join status shows a joined device with a real user email.
        // Detected via CollectAadJoinStatus() in DeviceInfoCollector.
        private bool _aadJoinedWithUser;
        // Soft indicator: AAD userEmail matches a transient provisioning placeholder pattern
        // (foouser@, autopilot@). These accounts appear during Autopilot pre-provisioning
        // (WhiteGlove) and MUST NOT be treated as a real AAD-joined user.
        private bool _fooUserDetected;

        // Set to true the moment the Shell-Core Event-62407 "WhiteGlove_Success" handler fires.
        // Distinct from <see cref="_signalCorrelatedWhiteGloveTriggered"/> (which is the
        // fire-once guard inside CompleteWhiteGlove and covers any completion path). Feeds
        // the WhiteGloveClassifier as the strongest positive signal.
        private bool _shellCoreWhiteGloveFired;

        // Signal-correlated WhiteGlove detection (Ebene 2.6):
        // Feeds the bestehenden OnWhiteGloveCompleted() callback when the Shell-Core detector
        // does not match but the session shows the WhiteGlove sealing pattern (all Device-apps done,
        // DeviceSetup category succeeded, system reboot observed, AAD Not Joined, no AccountSetup
        // progress, no desktop arrival). Requires ≥10 min stability to avoid false-positives.
        private DateTime? _signalCorrelatedWhiteGloveStableSince;
        private bool _signalCorrelatedWhiteGloveTriggered;
        private bool _systemRebootObserved;
        private const int SignalCorrelatedWhiteGloveStabilityMinutes = 10;

        /// <summary>
        /// Called by MonitoringService when a system_reboot_detected event is emitted
        /// (agent restart after an unclean exit / machine reboot). Used as an input signal
        /// for the signal-correlated WhiteGlove detection heuristic.
        /// </summary>
        public void NotifySystemRebootDetected()
        {
            lock (_stateLock)
            {
                _systemRebootObserved = true;
            }
        }

        /// <summary>
        /// Called after a successful RegisterSession. Compares the backend's authoritative
        /// validator verdict against the agent's registry-based enrollment-type detection
        /// and, on mismatch, emits an <c>enrollment_type_mismatch</c> warning event and
        /// switches the active flow handler to the backend's verdict.
        ///
        /// <see cref="ValidatorType.CorporateIdentifier"/> and <see cref="ValidatorType.Bootstrap"/>
        /// are treated as type-neutral (no claim about Classic vs DevPrep) and do not trigger a switch.
        /// </summary>
        public void ReconcileWithBackendValidator(ValidatorType validatedBy)
        {
            EnrollmentType backendType = validatedBy switch
            {
                ValidatorType.AutopilotV1 => EnrollmentType.Classic,
                ValidatorType.DeviceAssociation => EnrollmentType.DevicePreparation,
                _ => EnrollmentType.Unknown
            };

            if (backendType == EnrollmentType.Unknown)
                return;

            string currentWire;
            lock (_stateLock) { currentWire = _enrollmentType; }
            var currentType = EnrollmentTypeExtensions.FromWireFormat(currentWire);

            if (currentType == backendType)
            {
                _logger.Verbose($"EnrollmentTracker: backend validator '{validatedBy}' matches registry-detected enrollment type '{currentWire}'.");
                return;
            }

            var newWire = backendType.ToWireFormat();
            _logger.Warning($"EnrollmentTracker: enrollment type mismatch — registry='{currentWire}', backend='{newWire}' (validator={validatedBy}). Switching to backend verdict.");

            lock (_stateLock)
            {
                _enrollmentType = newWire;
                _flowHandler = EnrollmentFlowFactory.Create(backendType);
                _stateData.EnrollmentType = newWire;
                _stateDirty = true;
            }

            _emitEvent(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = "enrollment_type_mismatch",
                Severity = EventSeverity.Warning,
                Source = "EnrollmentTracker",
                Phase = EnrollmentPhase.Unknown,
                Message = $"Registry-detected enrollment type '{currentWire}' differs from backend validator verdict '{newWire}' ({validatedBy}). Using backend verdict.",
                Data = new Dictionary<string, object>
                {
                    { "registryEnrollmentType", currentWire },
                    { "backendEnrollmentType", newWire },
                    { "backendValidator", validatedBy.ToString() }
                }
            });
        }

        // True when no interactive user session is expected.
        // Self-Deploying (AutopilotMode=1) is always device-only.
        // SkipUserStatusPage=true alone does NOT mean device-only — admins commonly skip
        // the user ESP page in user-driven enrollments. Only classify as device-only when
        // SkipUserStatusPage=true AND no user has AAD-joined (no aad_join_status with userEmail).
        private bool IsDeviceOnlyDeployment => IsSelfDeploying || (_skipUserStatusPage == true && !_aadJoinedWithUser);

        /// <summary>
        /// Creates a read-only snapshot of current state for the shadow state machine.
        /// Must be called OUTSIDE _stateLock (takes its own lock internally).
        /// </summary>
        private CompletionContext SnapshotCompletionContext()
        {
            lock (_stateLock)
            {
                return new CompletionContext
                {
                    EnrollmentType = _enrollmentType,
                    IsHybridJoin = _isHybridJoin,
                    AutopilotMode = _autopilotMode,
                    SkipUserStatusPage = _skipUserStatusPage,
                    AadJoinedWithUser = _aadJoinedWithUser,
                    EspEverSeen = _espEverSeen,
                    EspFinalExitSeen = _espFinalExitSeen,
                    DesktopArrived = _desktopArrived,
                    LastEspPhase = _lastEspPhase,
                    DeviceInfoCollected = _deviceInfoCollected,
                    HasHelloTracker = _espAndHelloTracker != null,
                    IsHelloCompleted = _espAndHelloTracker?.IsHelloCompleted ?? false,
                    IsHelloPolicyConfigured = _espAndHelloTracker?.IsPolicyConfigured ?? false,
                    HasUnresolvedEspCategories = false, // Will be set by caller when available
                    HasAccountSetupActivity = _espAndHelloTracker?.HasAccountSetupActivity ?? false,
                    WhiteGloveStartDetected = _espAndHelloTracker?.IsWhiteGloveStartDetected ?? false,
                    HasSaveWhiteGloveSuccessResult = _espAndHelloTracker?.HasSaveWhiteGloveSuccessResult ?? false,
                    ShellCoreWhiteGloveSuccess = _shellCoreWhiteGloveFired,
                    IsFooUserDetected = _fooUserDetected,
                    AgentStartTimeUtc = _agentStartTimeUtc,
                    EspFinalExitUtc = _stateData.EspFinalExitUtc,
                    ImePatternSeenUtc = _stateData.ImePatternSeenUtc
                };
            }
        }

        /// <summary>
        /// Fires the shadow state machine trigger and compares the result with the actual state.
        /// Logs discrepancies as warnings. Does NOT change actual behavior.
        /// Also dual-writes the CompletionState to _stateData for persistence.
        /// </summary>
        private void ShadowProcessTrigger(string trigger, CompletionContext ctx = null)
        {
            try
            {
                ctx = ctx ?? SnapshotCompletionContext();
                var result = _completionSm.ProcessTrigger(trigger, ctx);

                // Dual-write: persist the explicit state alongside the boolean flags
                lock (_stateLock)
                {
                    _stateData.CompletionState = _completionSm.CurrentState.ToString();
                }

                // Compare terminal state: shadow SM reached terminal vs actual _enrollmentCompleteEmitted.
                // Both sides must use the same definition of "terminal": the actual flag is set for
                // enrollment_complete, enrollment_failed AND the WG-redirect path — so shadow must match
                // that by accepting Completed, Failed and WhiteGloveCompleted as terminal states.
                bool actualTerminal;
                lock (_stateLock) { actualTerminal = _enrollmentCompleteEmitted; }
                bool shadowTerminal = _completionSm.CurrentState.IsTerminal();

                if (actualTerminal != shadowTerminal)
                {
                    _logger.Warning($"EnrollmentTracker [SHADOW DISCREPANCY]: trigger='{trigger}', " +
                        $"shadowState={_completionSm.CurrentState}, actualEmitted={actualTerminal}, " +
                        $"shadowCompleted={shadowTerminal}");

                    // TEMPORARY: shadow SM rollout verification — remove when CompletionStateMachine is promoted to primary
                    // Emit a backend event (throttled to 1x per unique signature per session) so we can query
                    // mismatches without relying on client-side logs. Severity=Warning so it ships independent of SendTraceEvents.
                    EmitShadowDiscrepancyEvent(trigger, ctx, actualTerminal, shadowTerminal);
                }
                else if (result.Transitioned)
                {
                    _logger.Verbose($"EnrollmentTracker [SHADOW]: trigger='{trigger}', " +
                        $"{result.PreviousState} -> {result.NewState}");
                }
            }
            catch (Exception ex)
            {
                // Shadow mode must never crash the agent
                _logger.Debug($"EnrollmentTracker [SHADOW ERROR]: trigger='{trigger}', error={ex.Message}");
            }
        }

        // Safety-net timer for waiting_for_hello state
        private Timer _waitingForHelloSafetyTimer;
        private const int WaitingForHelloSafetyTimeoutSeconds = 420; // 7 min — longer than Hello chain (330s)

        // ESP provisioning settle wait: when IME pattern fires but ESP categories are still unresolved,
        // wait up to 30s for Windows to finalize the ESP provisioning status in the registry.
        private bool _isWaitingForEspSettle = false;
        private Timer _waitingForEspSettleTimer;
        private const int EspSettleTimeoutSeconds = 30;

        // Safety-net timer for device-only ESP completion (SkipUserStatusPage=true)
        private Timer _deviceOnlyCompletionSafetyTimer;
        private const int DeviceOnlyCompletionSafetyTimeoutSeconds = 420; // 7 min — same as Hello safety

        // State persistence for crash recovery
        private readonly EnrollmentStatePersistence _statePersistence;
        private EnrollmentStateData _stateData = new EnrollmentStateData();
        private bool _stateDirty;

        // Completion check throttling (max 1x/min per source)
        private readonly Dictionary<string, DateTime> _lastCompletionCheckBySource = new Dictionary<string, DateTime>();

        // Shadow state machine: runs in parallel with existing logic for behavioral verification.
        // Logs discrepancies but does NOT affect actual enrollment completion.
        private readonly CompletionStateMachine _completionSm = new CompletionStateMachine();

        // Default IME log folder
        private const string DefaultImeLogFolder = @"%ProgramData%\Microsoft\IntuneManagementExtension\Logs";

        private Telemetry.DeviceInfo.DeviceInfoCollector _deviceInfoCollector;

        // ConfigMgr (SCCM) co-management detection — fire-once guard
        private bool _configMgrDetected;

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
            SystemSignals.EspAndHelloTracker espAndHelloTracker = null,
            bool isBootstrapMode = false,
            bool sendTraceEvents = true)
        {
            _isBootstrapMode = isBootstrapMode;
            _sendTraceEvents = sendTraceEvents;
            _isHybridJoin = DetectHybridJoinStatic();
            _sessionId = sessionId;
            _tenantId = tenantId;
            _emitEvent = emitEvent;
            _logger = logger;
            _imeLogPatterns = imeLogPatterns ?? new List<ImeLogPattern>();
            _imeLogFolder = imeLogFolderOverride ?? DefaultImeLogFolder;
            _espAndHelloTracker = espAndHelloTracker;
            _deviceInfoCollector = new Telemetry.DeviceInfo.DeviceInfoCollector(sessionId, tenantId, emitEvent, logger);

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
            _imeLogTracker.OnImeSessionChange = HandleImeSessionChange;
            _imeLogTracker.OnDoTelemetryReceived = HandleDoTelemetryReceived;
            _imeLogTracker.OnScriptCompleted = HandleScriptCompleted;

            // Subscribe to EspAndHelloTracker completion event if available
            if (_espAndHelloTracker != null)
            {
                _espAndHelloTracker.HelloCompleted += OnHelloCompleted;
                _espAndHelloTracker.FinalizingSetupPhaseTriggered += OnFinalizingSetupPhaseTriggered;
                _espAndHelloTracker.WhiteGloveCompleted += OnWhiteGloveCompleted;
                _espAndHelloTracker.EspFailureDetected += OnEspFailureDetected;
                _espAndHelloTracker.DeviceSetupProvisioningComplete += OnDeviceSetupProvisioningComplete;

                // Backfill recent ESP exit events that may have fired before we subscribed.
                // The EspAndHelloTracker is started by CollectorCoordinator before the EnrollmentTracker
                // is created, so esp_exiting events detected during that window are lost. Backfill recovers them.
                _espAndHelloTracker.BackfillRecentEspExitEvents();
            }
        }

        /// <summary>
        /// Updates the SendTraceEvents setting from remote config at runtime.
        /// </summary>
        public void UpdateSendTraceEvents(bool value) => _sendTraceEvents = value;

        /// <summary>
        /// Emits a Trace-severity event that captures an agent decision for backend-side troubleshooting.
        /// Always logged locally; only sent to the backend when SendTraceEvents is enabled.
        /// </summary>
        private void EmitTraceEvent(string decision, string reason, Dictionary<string, object> context = null)
        {
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

            _logger.Trace($"EnrollmentTracker: {decision} — {reason}");

            if (_sendTraceEvents)
            {
                _emitEvent(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId = _tenantId,
                    EventType = "agent_trace",
                    Severity = EventSeverity.Trace,
                    Source = "EnrollmentTracker",
                    Phase = EnrollmentPhase.Unknown,
                    Message = $"{decision}: {reason}",
                    Data = data
                });
            }
        }

        // TEMPORARY: shadow SM rollout verification — remove when CompletionStateMachine is promoted to primary
        /// <summary>
        /// Emits a <c>shadow_discrepancy</c> event when the shadow CompletionStateMachine disagrees
        /// with the actual <c>_enrollmentCompleteEmitted</c> flag. Throttled to one event per unique
        /// {trigger|shadowState|actualEmitted} signature per session to avoid flooding the backend
        /// while still surfacing every distinct divergence constellation.
        /// </summary>
        private void EmitShadowDiscrepancyEvent(string trigger, CompletionContext ctx, bool actualTerminal, bool shadowTerminal)
        {
            var shadowState = _completionSm.CurrentState.ToString();
            var signature = $"{trigger}|{shadowState}|{actualTerminal}";
            if (signature == _lastShadowDiscrepancySignature)
                return;
            _lastShadowDiscrepancySignature = signature;

            var data = new Dictionary<string, object>
            {
                { "trigger", trigger ?? "(null)" },
                { "shadowState", shadowState },
                { "actualEmitted", actualTerminal },
                { "shadowCompleted", shadowTerminal },
                { "enrollmentType", ctx?.EnrollmentType },
                { "autopilotMode", ctx?.AutopilotMode },
                { "skipUserStatusPage", ctx?.SkipUserStatusPage },
                { "aadJoinedWithUser", ctx?.AadJoinedWithUser ?? false },
                { "isHybridJoin", ctx?.IsHybridJoin ?? false },
                { "isDeviceOnly", ctx?.IsDeviceOnly ?? false },
                { "espEverSeen", ctx?.EspEverSeen ?? false },
                { "espFinalExitSeen", ctx?.EspFinalExitSeen ?? false },
                { "desktopArrived", ctx?.DesktopArrived ?? false },
                { "lastEspPhase", ctx?.LastEspPhase },
                { "hasHelloTracker", ctx?.HasHelloTracker ?? false },
                { "isHelloCompleted", ctx?.IsHelloCompleted ?? false },
                { "isHelloPolicyConfigured", ctx?.IsHelloPolicyConfigured ?? false },
                { "hasUnresolvedEspCategories", ctx?.HasUnresolvedEspCategories ?? false }
            };

            try
            {
                _emitEvent(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId = _tenantId,
                    EventType = Constants.EventTypes.ShadowDiscrepancy,
                    Severity = EventSeverity.Warning,
                    Source = "EnrollmentTracker",
                    Phase = EnrollmentPhase.Unknown,
                    Message = $"Shadow SM mismatch: trigger={trigger}, shadow={shadowState}, emitted={actualTerminal}",
                    Data = data
                });
            }
            catch (Exception ex)
            {
                // Diagnostic event must never crash the agent
                _logger.Debug($"EnrollmentTracker [SHADOW EVENT ERROR]: {ex.Message}");
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
            Task.Run(() =>
            {
                try
                {
                    CollectDeviceInfo();
                }
                catch (Exception ex)
                {
                    _logger.Warning($"EnrollmentTracker: background device info collection failed: {ex.Message}");
                }
            });

            // Check for ConfigMgr co-management client (separate task, must not delay device info)
            Task.Run(() =>
            {
                try { CheckConfigMgrClient("startup"); }
                catch (Exception ex) { _logger.Debug($"EnrollmentTracker: startup ConfigMgr check failed: {ex.Message}"); }
            });

            // TODO: Überdenken ob ein 30s timer hier wirklich immer gut ist, hab einen Fall gesehen mit 
            // laaanger Wartephase in WhiteGlove weil ein 24H2 feature update reinkam und installiert wurde

            // Start periodic summary timer (30s, starts when app tracking begins)
            _summaryTimer = new Timer(SummaryTimerCallback, null, Timeout.Infinite, Timeout.Infinite);
            _debugStateTimer = new Timer(DebugStateTimerCallback, null, Timeout.Infinite, Timeout.Infinite);

            _logger.Info("EnrollmentTracker: started");
        }

        /// <summary>
        /// Stops the enrollment tracker
        /// </summary>
        public void Stop()
        {
            _logger.Info("EnrollmentTracker: stopping");

            _summaryTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _debugStateTimer?.Change(Timeout.Infinite, Timeout.Infinite);
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

            bool wasDeviceOnly;
            lock (_stateLock)
            {
                wasDeviceOnly = IsDeviceOnlyDeployment;
                _enrollmentType = result.enrollmentType;
                _flowHandler = EnrollmentFlowFactory.FromWireFormat(result.enrollmentType);
                _skipUserStatusPage = result.skipUserStatusPage;
                _skipDeviceStatusPage = result.skipDeviceStatusPage;
                _autopilotMode = result.autopilotMode;
                _aadJoinedWithUser = result.hasAadJoinedUser;
                _fooUserDetected = result.isFooUserDetected;
                _stateData.EnrollmentType = _enrollmentType;
                _stateData.SkipUserStatusPage = _skipUserStatusPage;
                _stateData.SkipDeviceStatusPage = _skipDeviceStatusPage;
                _stateData.AutopilotMode = _autopilotMode;
                _stateData.AadJoinedWithUser = _aadJoinedWithUser;
                _stateData.FooUserDetected = _fooUserDetected;
                _stateData.IsHybridJoin = _isHybridJoin;
                _stateDirty = true;
            }

            // Snapshot values under lock for trace events (emitted outside lock)
            bool isDeviceOnly, isSelfDeploying, aadJoinedWithUser;
            int? autopilotMode;
            bool? skipUserStatusPage;
            string enrollmentType;
            lock (_stateLock)
            {
                isDeviceOnly = IsDeviceOnlyDeployment;
                isSelfDeploying = IsSelfDeploying;
                aadJoinedWithUser = _aadJoinedWithUser;
                autopilotMode = _autopilotMode;
                skipUserStatusPage = _skipUserStatusPage;
                enrollmentType = _enrollmentType;
            }

            _logger.Info($"EnrollmentTracker: enrollment type detected: {enrollmentType ?? "unknown"} " +
                         $"(autopilotMode={autopilotMode}, skipUserStatusPage={skipUserStatusPage}, skipDeviceStatusPage={result.skipDeviceStatusPage}, aadJoinedWithUser={aadJoinedWithUser})");

            // Emit trace when AAD join with user is detected and reclassifies away from device-only
            if (aadJoinedWithUser && wasDeviceOnly && !isDeviceOnly)
            {
                EmitTraceEvent("user_session_detected_via_aad_join",
                    "AAD join with user detected — user session active, using standard completion paths",
                    new Dictionary<string, object>
                    {
                        { "autopilotMode", autopilotMode },
                        { "skipUserStatusPage", skipUserStatusPage },
                        { "aadJoinedWithUser", true },
                        { "isDeviceOnlyDeployment", false }
                    });
            }

            if (isDeviceOnly)
            {
                var reason = isSelfDeploying
                    ? "AutopilotMode=1 (Self-Deploying)"
                    : $"SkipUserStatusPage=true (autopilotMode={autopilotMode})";
                EmitTraceEvent("device_only_deployment_detected",
                    $"{reason} — Hello guard will be bypassed, provisioning status used as completion signal",
                    new Dictionary<string, object>
                    {
                        { "autopilotMode", autopilotMode },
                        { "skipUserStatusPage", skipUserStatusPage },
                        { "enrollmentType", enrollmentType },
                        { "aadJoinedWithUser", aadJoinedWithUser },
                        { "isSelfDeploying", isSelfDeploying },
                        { "isDeviceOnlyDeployment", true }
                    });
            }

            // Mark device info as collected and re-evaluate any deferred completion signal
            _deviceInfoCollected = true;

            string pendingSource;
            lock (_stateLock) { pendingSource = _pendingCompletionSource; _pendingCompletionSource = null; }
            if (pendingSource != null)
            {
                _logger.Info($"EnrollmentTracker: re-evaluating deferred completion signal '{pendingSource}' now that device info is available " +
                             $"(isDeviceOnly={isDeviceOnly}, autopilotMode={autopilotMode}, skipUserStatusPage={skipUserStatusPage})");
                if (pendingSource == "device_setup_provisioning_complete")
                    OnDeviceSetupProvisioningComplete(this, EventArgs.Empty);
                else
                    TryEmitEnrollmentComplete(pendingSource);
            }

            // Shadow state machine: track device info collected
            ShadowProcessTrigger("device_info_collected");
        }

        /// <summary>
        /// Collects device info that may change during enrollment (e.g., BitLocker enabled via policy).
        /// Called at enrollment complete to capture final state.
        /// </summary>
        private void CollectDeviceInfoAtEnd()
            => _deviceInfoCollector.CollectAtEnd();

        /// <summary>
        /// Checks for ConfigMgr (SCCM) co-management client presence on the device.
        /// Fire-once: emits <c>configmgr_client_detected</c> event on first detection only.
        /// Called at startup, DeviceSetup, and AccountSetup — always via Task.Run to avoid blocking.
        /// </summary>
        private void CheckConfigMgrClient(string trigger)
        {
            if (_configMgrDetected) return;

            try
            {
                bool registryFound = false;
                bool directoryFound = false;
                string ccmVersion = null;
                string siteCode = null;
                string serviceState = "not_found";

                // 1. Registry: HKLM\SOFTWARE\Microsoft\CCM
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\CCM"))
                {
                    if (key != null)
                        registryFound = true;
                }

                // Version from CCMSetup
                using (var setupKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\CCMSetup"))
                    ccmVersion = setupKey?.GetValue("LastValidVersion")?.ToString();

                // SiteCode from SMS Mobile Client
                using (var smsKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\SMS\Mobile Client"))
                    siteCode = smsKey?.GetValue("AssignedSiteCode")?.ToString();

                // 2. Directory: C:\Windows\CCM
                directoryFound = Directory.Exists(@"C:\Windows\CCM");

                // 3. Service: CcmExec (via ServiceController, native .NET Framework 4.8)
                try
                {
                    using (var sc = new System.ServiceProcess.ServiceController("CcmExec"))
                        serviceState = sc.Status.ToString();
                }
                catch (InvalidOperationException) { } // service not installed

                // Compute confidence score — directory alone is too weak (could be remnant)
                int confidence = 0;
                if (directoryFound)              confidence += 10;
                if (registryFound)               confidence += 20;
                if (ccmVersion != null)          confidence += 15;
                if (siteCode != null)            confidence += 15;
                if (serviceState != "not_found") confidence += 20;
                if (serviceState == "Running")   confidence += 20;

                // Nothing found — no event
                if (confidence == 0)
                    return;

                _configMgrDetected = true;

                var versionSuffix = ccmVersion != null ? $" (v{ccmVersion})" : "";
                var siteSuffix = siteCode != null ? $", site {siteCode}" : "";
                var confidenceLabel = confidence >= 50 ? "high" : "low";

                _emitEvent(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId = _tenantId,
                    EventType = "configmgr_client_detected",
                    Severity = EventSeverity.Info,
                    Source = "Agent",
                    Phase = EnrollmentPhase.Unknown,
                    Message = $"ConfigMgr (SCCM) client detected ({confidenceLabel} confidence: {confidence}/100) — co-managed device{versionSuffix}{siteSuffix}",
                    Data = new Dictionary<string, object>
                    {
                        { "ccmRegistryFound", registryFound },
                        { "ccmDirectoryFound", directoryFound },
                        { "ccmServiceState", serviceState },
                        { "ccmVersion", ccmVersion ?? "unknown" },
                        { "siteCode", siteCode ?? "unknown" },
                        { "confidenceScore", confidence },
                        { "detectedAt", trigger }
                    }
                });

                _logger.Info($"ConfigMgr client detected (trigger={trigger}, confidence={confidence}, service={serviceState}, version={ccmVersion ?? "?"})");
            }
            catch (Exception ex)
            {
                _logger.Debug($"EnrollmentTracker: ConfigMgr check failed: {ex.Message}");
            }
        }

    }
}
