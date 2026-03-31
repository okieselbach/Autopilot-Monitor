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

namespace AutopilotMonitor.Agent.Core.Monitoring.Tracking
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
        private readonly Collectors.EspAndHelloTracker _espAndHelloTracker;

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
        private bool _isWaitingForHello = false; // Track if we're waiting for Hello to complete before sending enrollment_complete
        private readonly bool _isBootstrapMode; // Agent started via bootstrap token (pre-MDM)
        private bool _sendTraceEvents = true; // Send Trace-severity events to backend for decision auditing
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

        // True when no interactive user session is expected.
        // Self-Deploying (AutopilotMode=1) is always device-only.
        // SkipUserStatusPage=true alone does NOT mean device-only — admins commonly skip
        // the user ESP page in user-driven enrollments. Only classify as device-only when
        // SkipUserStatusPage=true AND no user has AAD-joined (no aad_join_status with userEmail).
        private bool IsDeviceOnlyDeployment => IsSelfDeploying || (_skipUserStatusPage == true && !_aadJoinedWithUser);

        // Safety-net timer for waiting_for_hello state
        private Timer _waitingForHelloSafetyTimer;
        private const int WaitingForHelloSafetyTimeoutSeconds = 420; // 7 min — longer than Hello chain (330s)

        // Safety-net timer for device-only ESP completion (SkipUserStatusPage=true)
        private Timer _deviceOnlyCompletionSafetyTimer;
        private const int DeviceOnlyCompletionSafetyTimeoutSeconds = 420; // 7 min — same as Hello safety

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
            Collectors.EspAndHelloTracker espAndHelloTracker = null,
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
                _skipUserStatusPage = result.skipUserStatusPage;
                _skipDeviceStatusPage = result.skipDeviceStatusPage;
                _autopilotMode = result.autopilotMode;
                _aadJoinedWithUser = result.hasAadJoinedUser;
                _stateData.EnrollmentType = _enrollmentType;
                _stateData.SkipUserStatusPage = _skipUserStatusPage;
                _stateData.SkipDeviceStatusPage = _skipDeviceStatusPage;
                _stateData.AutopilotMode = _autopilotMode;
                _stateData.AadJoinedWithUser = _aadJoinedWithUser;
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
        }

        /// <summary>
        /// Collects device info that may change during enrollment (e.g., BitLocker enabled via policy).
        /// Called at enrollment complete to capture final state.
        /// </summary>
        private void CollectDeviceInfoAtEnd()
            => _deviceInfoCollector.CollectAtEnd();

    }
}
