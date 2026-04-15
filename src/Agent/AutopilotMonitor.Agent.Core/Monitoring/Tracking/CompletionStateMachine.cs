using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Agent.Core.Monitoring.Tracking
{
    /// <summary>
    /// Read-only snapshot of all state needed for guard evaluation and transition decisions.
    /// Populated under lock in the caller, consumed outside lock by the state machine.
    /// Preserves the existing thread-safety pattern from EnrollmentTracker.
    /// </summary>
    internal class CompletionContext
    {
        // Deployment classification
        public string EnrollmentType { get; set; }
        public int? AutopilotMode { get; set; }
        public bool? SkipUserStatusPage { get; set; }
        public bool AadJoinedWithUser { get; set; }
        public bool IsHybridJoin { get; set; }

        // Computed deployment classification (mirrors IsDeviceOnlyDeployment property)
        public bool IsDeviceOnly => CompletionGuards.IsDeviceOnlyDeployment(
            AutopilotMode, SkipUserStatusPage, AadJoinedWithUser);

        // Signal state
        public bool EspEverSeen { get; set; }
        public bool EspFinalExitSeen { get; set; }
        public bool DesktopArrived { get; set; }
        public string LastEspPhase { get; set; }
        public bool DeviceInfoCollected { get; set; }

        // Hello state (from EspAndHelloTracker)
        public bool HasHelloTracker { get; set; }
        public bool IsHelloCompleted { get; set; }
        public bool IsHelloPolicyConfigured { get; set; }
        public bool HasUnresolvedEspCategories { get; set; }

        // Timing
        public DateTime AgentStartTimeUtc { get; set; }
        public DateTime? EspFinalExitUtc { get; set; }
        public DateTime? ImePatternSeenUtc { get; set; }

        // Trigger metadata
        public string Source { get; set; }
    }

    /// <summary>
    /// Result of a state machine transition. Contains the new state and metadata
    /// about what actions the caller should take (emit events, start timers, etc.).
    /// The state machine itself is side-effect-free — all actions are communicated
    /// via this result object to preserve the lock-free emission pattern.
    /// </summary>
    internal class TransitionResult
    {
        public static readonly TransitionResult NoTransition = new TransitionResult
        {
            Transitioned = false,
            NewState = EnrollmentCompletionState.Idle
        };

        public bool Transitioned { get; set; }
        public EnrollmentCompletionState NewState { get; set; }
        public EnrollmentCompletionState PreviousState { get; set; }
        public string CompletionSource { get; set; }

        // Action flags — caller executes these OUTSIDE lock
        public bool ShouldEmitEnrollmentComplete { get; set; }
        public bool ShouldEmitEnrollmentFailed { get; set; }
        public bool ShouldEmitWhiteGloveComplete { get; set; }
        public bool ShouldStartHelloWaitTimer { get; set; }
        public bool ShouldStartHelloSafetyTimer { get; set; }
        public bool ShouldStartEspSettleTimer { get; set; }
        public bool ShouldStartDeviceOnlyEspTimer { get; set; }
        public bool ShouldStartDeviceOnlySafetyTimer { get; set; }
        public bool ShouldForceMarkHelloCompleted { get; set; }
        public bool ShouldResetEspForResumption { get; set; }
        public bool ShouldPersistState { get; set; }
        public string FailureType { get; set; }
        public string DeferredSource { get; set; }

        // Signals to record
        public List<string> SignalsToRecord { get; set; }
    }

    /// <summary>
    /// Explicit state machine for enrollment completion logic.
    /// Replaces the implicit boolean-flag-based state tracking in CompletionLogic.cs.
    ///
    /// Thread safety: The ProcessTrigger method is thread-safe. State reads and transitions
    /// happen under a single lock (microsecond-level critical sections). All side effects
    /// (event emission, timer management) are communicated via TransitionResult and executed
    /// by the caller OUTSIDE the lock — identical pattern to the original code.
    ///
    /// This class is intentionally side-effect-free: it computes state transitions and
    /// returns action flags. The caller (EnrollmentTracker) owns the actual side effects.
    /// </summary>
    internal class CompletionStateMachine
    {
        private readonly object _lock = new object();
        private EnrollmentCompletionState _currentState = EnrollmentCompletionState.Idle;
        private string _deferredSource;

        // Signal state (mirrored from EnrollmentTracker fields)
        private bool _espEverSeen;
        private bool _espFinalExitSeen;
        private bool _desktopArrived;
        private bool _isWaitingForHello;
        private bool _isWaitingForEspSettle;

        public EnrollmentCompletionState CurrentState
        {
            get { lock (_lock) { return _currentState; } }
        }

        // Expose internal state for shadow-mode comparison and persistence
        internal bool EspEverSeen { get { lock (_lock) { return _espEverSeen; } } }
        internal bool EspFinalExitSeen { get { lock (_lock) { return _espFinalExitSeen; } } }
        internal bool DesktopArrived { get { lock (_lock) { return _desktopArrived; } } }

        /// <summary>
        /// Reconstructs state from persisted EnrollmentStateData (crash recovery).
        /// Must be called before Start() if recovering from a crash.
        /// </summary>
        public void RestoreState(EnrollmentCompletionState state, bool espEverSeen,
            bool espFinalExitSeen, bool desktopArrived, bool isWaitingForHello,
            bool isWaitingForEspSettle, string deferredSource)
        {
            lock (_lock)
            {
                _currentState = state;
                _espEverSeen = espEverSeen;
                _espFinalExitSeen = espFinalExitSeen;
                _desktopArrived = desktopArrived;
                _isWaitingForHello = isWaitingForHello;
                _isWaitingForEspSettle = isWaitingForEspSettle;
                _deferredSource = deferredSource;
            }
        }

        /// <summary>
        /// Reconstructs the explicit state from boolean flags (backwards compatibility).
        /// Used when loading state from an old-format persistence file that doesn't
        /// contain the CompletionState field.
        /// </summary>
        public static EnrollmentCompletionState ReconstructStateFromFlags(
            bool enrollmentCompleteEmitted, bool isWaitingForHello, bool isWaitingForEspSettle,
            bool espFinalExitSeen, bool desktopArrived, bool espEverSeen,
            bool isDeviceOnly)
        {
            if (enrollmentCompleteEmitted)
                return EnrollmentCompletionState.Completed;
            if (isWaitingForHello)
                return EnrollmentCompletionState.WaitingForHello;
            if (isWaitingForEspSettle)
                return EnrollmentCompletionState.WaitingForEspSettle;
            if (isDeviceOnly && espFinalExitSeen)
                return EnrollmentCompletionState.DeviceOnlyAwaitingCompletion;
            if (espFinalExitSeen && desktopArrived)
                return EnrollmentCompletionState.DesktopArrivedAwaitingHello;
            if (espFinalExitSeen)
                return EnrollmentCompletionState.EspExitedAwaitingCompletion;
            if (espEverSeen && desktopArrived)
                return EnrollmentCompletionState.DesktopArrivedEspBlocking;
            if (espEverSeen)
                return EnrollmentCompletionState.EspActive;
            if (desktopArrived)
                return EnrollmentCompletionState.DesktopArrivedAwaitingHello;
            return EnrollmentCompletionState.Idle;
        }

        /// <summary>
        /// Core state machine: processes a trigger against the current state and context.
        /// Returns a TransitionResult describing what happened and what actions to take.
        ///
        /// The state transition happens atomically under lock.
        /// All side-effect instructions are in the returned result — caller executes them outside lock.
        /// </summary>
        public TransitionResult ProcessTrigger(string trigger, CompletionContext ctx)
        {
            if (string.IsNullOrEmpty(trigger))
                return TransitionResult.NoTransition;

            lock (_lock)
            {
                // Sync internal signal flags from the authoritative tracker snapshot.
                // Monoton false→true: these signals are stable-once-seen. The single
                // intentional reset (HandleEspResumed clearing _espFinalExitSeen) runs
                // AFTER this sync inside its own switch case, so it remains the last writer.
                // Without this, the SM can drift from the tracker (e.g. when
                // _stateData.EspEverSeen=true was set in memory but Save() hadn't run
                // before a reboot — restore loads false and downstream guards misfire).
                if (ctx != null)
                {
                    if (ctx.EspEverSeen)      _espEverSeen = true;
                    if (ctx.EspFinalExitSeen) _espFinalExitSeen = true;
                    if (ctx.DesktopArrived)   _desktopArrived = true;
                }

                // Terminal states: no transitions allowed
                if (_currentState.IsTerminal())
                    return TransitionResult.NoTransition;

                switch (trigger)
                {
                    case "esp_phase_changed":
                        return HandleEspPhaseChanged(ctx);

                    case "esp_exiting":
                        return HandleEspExiting(ctx);

                    case "desktop_arrived":
                        return HandleDesktopArrived(ctx);

                    case "hello_completed":
                        return HandleHelloCompleted(ctx);

                    case "ime_user_session_completed":
                        return HandleImeUserSessionCompleted(ctx);

                    case "device_setup_provisioning_complete":
                        return HandleDeviceSetupProvisioningComplete(ctx);

                    case "hello_safety_timeout":
                        return HandleHelloSafetyTimeout(ctx);

                    case "esp_settle_timeout":
                        return HandleEspSettleTimeout(ctx);

                    case "device_only_esp_timer_expired":
                        return HandleDeviceOnlyEspTimerExpired(ctx);

                    case "device_only_safety_timeout":
                        return HandleDeviceOnlySafetyTimeout(ctx);

                    case "esp_failure_terminal":
                        return HandleEspFailureTerminal(ctx);

                    case "esp_failure_grace_expired":
                        return HandleEspFailureTerminal(ctx);

                    case "whiteglove_complete":
                        return HandleWhiteGloveComplete(ctx);

                    case "esp_resumed":
                        return HandleEspResumed(ctx);

                    case "device_info_collected":
                        return HandleDeviceInfoCollected(ctx);

                    default:
                        return TransitionResult.NoTransition;
                }
            }
        }

        // ===== Trigger Handlers (all called under _lock) =====

        private TransitionResult HandleEspPhaseChanged(CompletionContext ctx)
        {
            if (_currentState != EnrollmentCompletionState.Idle
                && _currentState != EnrollmentCompletionState.EspActive)
                return TransitionResult.NoTransition;

            var prev = _currentState;
            _espEverSeen = true;
            _currentState = EnrollmentCompletionState.EspActive;

            return new TransitionResult
            {
                Transitioned = prev != _currentState,
                PreviousState = prev,
                NewState = _currentState,
                SignalsToRecord = new List<string> { "esp_phase_changed" }
            };
        }

        private TransitionResult HandleEspExiting(CompletionContext ctx)
        {
            // ESP exiting is only relevant when ESP has been seen
            if (!_espEverSeen)
                return TransitionResult.NoTransition;

            var prev = _currentState;
            var result = new TransitionResult { PreviousState = prev, SignalsToRecord = new List<string>() };

            // Path A: AccountSetup completed or desktop already arrived
            if (string.Equals(ctx.LastEspPhase, "AccountSetup", StringComparison.OrdinalIgnoreCase)
                || _desktopArrived)
            {
                _espFinalExitSeen = true;
                _currentState = EnrollmentCompletionState.EspExitedAwaitingCompletion;
                result.SignalsToRecord.Add("esp_final_exit");
                result.ShouldStartHelloWaitTimer = true;
                result.ShouldPersistState = true;

                // Check if we can complete immediately
                if (CanComplete(ctx, "esp_hello_composite"))
                {
                    _currentState = EnrollmentCompletionState.Completed;
                    result.ShouldEmitEnrollmentComplete = true;
                    result.CompletionSource = "esp_hello_composite";
                }
                else if (ctx.IsHybridJoin && CompletionGuards.IsHybridRebootGateBlocking(
                    ctx.IsHybridJoin, "esp_hello_composite",
                    ctx.ImePatternSeenUtc, ctx.EspFinalExitUtc, ctx.AgentStartTimeUtc))
                {
                    _currentState = EnrollmentCompletionState.HybridRebootGateBlocked;
                }
            }
            // Path B: Registry definitively says device-only
            else if (ctx.SkipUserStatusPage == true)
            {
                _espFinalExitSeen = true;
                _currentState = EnrollmentCompletionState.DeviceOnlyAwaitingCompletion;
                result.SignalsToRecord.Add("device_only_esp_registry");
                result.ShouldStartHelloWaitTimer = true;
                result.ShouldPersistState = true;

                if (CanComplete(ctx, "device_only_esp_registry"))
                {
                    _currentState = EnrollmentCompletionState.Completed;
                    result.ShouldEmitEnrollmentComplete = true;
                    result.CompletionSource = "device_only_esp_registry";
                }
                else
                {
                    _currentState = EnrollmentCompletionState.DeviceOnlySafetyWait;
                    result.ShouldStartDeviceOnlySafetyTimer = true;
                }
            }
            // Path C: Unknown — start device-only detection timer
            else
            {
                result.ShouldStartDeviceOnlyEspTimer = true;
                // State stays as-is (EspActive or DesktopArrivedEspBlocking)
            }

            result.Transitioned = prev != _currentState;
            result.NewState = _currentState;
            return result;
        }

        private TransitionResult HandleDesktopArrived(CompletionContext ctx)
        {
            if (_desktopArrived)
                return TransitionResult.NoTransition;

            var prev = _currentState;
            _desktopArrived = true;

            var result = new TransitionResult
            {
                PreviousState = prev,
                ShouldPersistState = true,
                SignalsToRecord = new List<string> { "desktop_arrived" }
            };

            // SkipUserStatusPage handling: if ESP seen and SkipUser=true but no final exit yet
            if (_espEverSeen && ctx.SkipUserStatusPage == true && !_espFinalExitSeen)
            {
                _espFinalExitSeen = true;
                result.SignalsToRecord.Add("desktop_arrived_skip_user");
            }

            // Determine target state based on ESP status
            if (_espEverSeen && !_espFinalExitSeen)
            {
                // ESP still active — gate blocks
                _currentState = EnrollmentCompletionState.DesktopArrivedEspBlocking;
            }
            else
            {
                // No ESP blocking — can proceed toward completion
                _currentState = EnrollmentCompletionState.DesktopArrivedAwaitingHello;

                // Start Hello wait timer if needed (no active ESP, not device-only)
                if (!ctx.IsDeviceOnly && ctx.HasHelloTracker && !ctx.IsHelloCompleted)
                {
                    result.ShouldStartHelloWaitTimer = true;
                }

                // Try immediate completion
                if (CanComplete(ctx, "desktop_arrival"))
                {
                    _currentState = EnrollmentCompletionState.Completed;
                    result.ShouldEmitEnrollmentComplete = true;
                    result.CompletionSource = "desktop_arrival";
                }
            }

            result.Transitioned = prev != _currentState;
            result.NewState = _currentState;
            return result;
        }

        private TransitionResult HandleHelloCompleted(CompletionContext ctx)
        {
            var prev = _currentState;
            var result = new TransitionResult
            {
                PreviousState = prev,
                SignalsToRecord = new List<string> { "hello_resolved" }
            };

            if (_isWaitingForHello)
            {
                // IME path: was waiting for Hello
                _isWaitingForHello = false;
                if (CanCompleteWithHelloResolved(ctx, "ime_hello"))
                {
                    _currentState = EnrollmentCompletionState.Completed;
                    result.ShouldEmitEnrollmentComplete = true;
                    result.CompletionSource = "ime_hello";
                }
            }
            else if (_espFinalExitSeen)
            {
                // ESP composite path
                string source = "esp_hello_composite";
                if (ctx.IsHybridJoin && CompletionGuards.IsHybridRebootGateBlocking(
                    ctx.IsHybridJoin, source,
                    ctx.ImePatternSeenUtc, ctx.EspFinalExitUtc, ctx.AgentStartTimeUtc))
                {
                    _currentState = EnrollmentCompletionState.HybridRebootGateBlocked;
                }
                else
                {
                    _currentState = EnrollmentCompletionState.Completed;
                    result.ShouldEmitEnrollmentComplete = true;
                    result.CompletionSource = source;
                }
            }
            else if (_desktopArrived)
            {
                // Desktop + Hello path
                string source = "desktop_hello";
                if (!CompletionGuards.IsEspGateBlocking(source, ctx.EnrollmentType,
                    _espEverSeen, _espFinalExitSeen))
                {
                    _currentState = EnrollmentCompletionState.Completed;
                    result.ShouldEmitEnrollmentComplete = true;
                    result.CompletionSource = source;
                }
            }

            result.Transitioned = prev != _currentState;
            result.NewState = _currentState;
            return result;
        }

        private TransitionResult HandleImeUserSessionCompleted(CompletionContext ctx)
        {
            var prev = _currentState;
            var result = new TransitionResult
            {
                PreviousState = prev,
                SignalsToRecord = new List<string> { "ime_pattern" }
            };

            // Device-only: skip Hello entirely
            if (ctx.IsDeviceOnly)
            {
                _currentState = EnrollmentCompletionState.Completed;
                result.ShouldEmitEnrollmentComplete = true;
                result.CompletionSource = "ime_pattern";
                result.Transitioned = true;
                result.NewState = _currentState;
                return result;
            }

            // Hello configured but not yet completed — wait
            if (ctx.HasHelloTracker && ctx.IsHelloPolicyConfigured && !ctx.IsHelloCompleted)
            {
                _isWaitingForHello = true;
                _currentState = EnrollmentCompletionState.WaitingForHello;
                result.ShouldStartHelloWaitTimer = true;
                result.ShouldStartHelloSafetyTimer = true;
                result.ShouldPersistState = true;
                result.Transitioned = true;
                result.NewState = _currentState;
                return result;
            }

            // ESP categories unresolved — wait for settle
            if (ctx.HasHelloTracker && ctx.HasUnresolvedEspCategories)
            {
                _isWaitingForEspSettle = true;
                _currentState = EnrollmentCompletionState.WaitingForEspSettle;
                result.ShouldStartEspSettleTimer = true;
                result.ShouldPersistState = true;
                result.Transitioned = true;
                result.NewState = _currentState;
                return result;
            }

            // All clear — complete
            _currentState = EnrollmentCompletionState.Completed;
            result.ShouldEmitEnrollmentComplete = true;
            result.CompletionSource = "ime_pattern";
            result.Transitioned = true;
            result.NewState = _currentState;
            return result;
        }

        private TransitionResult HandleDeviceSetupProvisioningComplete(CompletionContext ctx)
        {
            var prev = _currentState;
            var result = new TransitionResult
            {
                PreviousState = prev,
                SignalsToRecord = new List<string> { "device_setup_provisioning_complete" }
            };

            if (!ctx.IsDeviceOnly)
            {
                // Not device-only: check if device info collected yet
                if (!ctx.DeviceInfoCollected)
                {
                    _deferredSource = "device_setup_provisioning_complete";
                    _currentState = EnrollmentCompletionState.DeferredForDeviceInfo;
                    result.DeferredSource = _deferredSource;
                    result.Transitioned = true;
                    result.NewState = _currentState;
                    return result;
                }
                // Not device-only and device info collected: normal paths handle it
                return TransitionResult.NoTransition;
            }

            // Device-only: mark ESP as seen and exited
            if (!_espEverSeen) _espEverSeen = true;
            if (!_espFinalExitSeen)
            {
                _espFinalExitSeen = true;
                result.SignalsToRecord.Add("self_deploying_esp_final_exit");
            }
            result.ShouldPersistState = true;

            _currentState = EnrollmentCompletionState.Completed;
            result.ShouldEmitEnrollmentComplete = true;
            result.CompletionSource = "self_deploying_provisioning_complete";
            result.Transitioned = true;
            result.NewState = _currentState;
            return result;
        }

        private TransitionResult HandleHelloSafetyTimeout(CompletionContext ctx)
        {
            if (!_isWaitingForHello)
                return TransitionResult.NoTransition;

            var prev = _currentState;
            _isWaitingForHello = false;
            _currentState = EnrollmentCompletionState.Completed;

            return new TransitionResult
            {
                Transitioned = true,
                PreviousState = prev,
                NewState = _currentState,
                ShouldForceMarkHelloCompleted = true,
                ShouldEmitEnrollmentComplete = true,
                CompletionSource = "ime_hello_safety_timeout",
                SignalsToRecord = new List<string> { "ime_hello_safety_timeout" }
            };
        }

        private TransitionResult HandleEspSettleTimeout(CompletionContext ctx)
        {
            if (!_isWaitingForEspSettle)
                return TransitionResult.NoTransition;

            var prev = _currentState;
            _isWaitingForEspSettle = false;
            _currentState = EnrollmentCompletionState.Completed;

            return new TransitionResult
            {
                Transitioned = true,
                PreviousState = prev,
                NewState = _currentState,
                ShouldEmitEnrollmentComplete = true,
                CompletionSource = "ime_pattern",
                SignalsToRecord = new List<string> { "esp_provisioning_settled" }
            };
        }

        private TransitionResult HandleDeviceOnlyEspTimerExpired(CompletionContext ctx)
        {
            // AccountSetup started meanwhile? Timer is obsolete
            if (string.Equals(ctx.LastEspPhase, "AccountSetup", StringComparison.OrdinalIgnoreCase))
                return TransitionResult.NoTransition;

            if (!_desktopArrived)
                return TransitionResult.NoTransition; // Wait for desktop

            var prev = _currentState;
            _espFinalExitSeen = true;
            _currentState = EnrollmentCompletionState.DesktopArrivedAwaitingHello;

            return new TransitionResult
            {
                Transitioned = true,
                PreviousState = prev,
                NewState = _currentState,
                ShouldStartHelloWaitTimer = true,
                ShouldPersistState = true,
                SignalsToRecord = new List<string> { "device_only_esp_final_exit" }
            };
        }

        private TransitionResult HandleDeviceOnlySafetyTimeout(CompletionContext ctx)
        {
            var prev = _currentState;
            _currentState = EnrollmentCompletionState.Completed;

            return new TransitionResult
            {
                Transitioned = true,
                PreviousState = prev,
                NewState = _currentState,
                ShouldForceMarkHelloCompleted = true,
                ShouldEmitEnrollmentComplete = true,
                CompletionSource = "device_only_esp_safety_timeout",
                SignalsToRecord = new List<string> { "device_only_esp_safety_timeout" }
            };
        }

        private TransitionResult HandleEspFailureTerminal(CompletionContext ctx)
        {
            var prev = _currentState;
            _currentState = EnrollmentCompletionState.Failed;

            return new TransitionResult
            {
                Transitioned = true,
                PreviousState = prev,
                NewState = _currentState,
                ShouldEmitEnrollmentFailed = true,
                FailureType = ctx.Source
            };
        }

        private TransitionResult HandleWhiteGloveComplete(CompletionContext ctx)
        {
            var prev = _currentState;
            _currentState = EnrollmentCompletionState.WhiteGloveCompleted;

            return new TransitionResult
            {
                Transitioned = true,
                PreviousState = prev,
                NewState = _currentState,
                ShouldEmitWhiteGloveComplete = true
            };
        }

        private TransitionResult HandleEspResumed(CompletionContext ctx)
        {
            if (!ctx.IsHybridJoin || !_espFinalExitSeen)
                return TransitionResult.NoTransition;

            var prev = _currentState;
            _espFinalExitSeen = false;
            _currentState = EnrollmentCompletionState.EspActive;

            return new TransitionResult
            {
                Transitioned = true,
                PreviousState = prev,
                NewState = _currentState,
                ShouldResetEspForResumption = true,
                ShouldPersistState = true,
                SignalsToRecord = new List<string> { "esp_resumed" }
            };
        }

        private TransitionResult HandleDeviceInfoCollected(CompletionContext ctx)
        {
            if (_currentState != EnrollmentCompletionState.DeferredForDeviceInfo)
                return TransitionResult.NoTransition;

            var prev = _currentState;
            var deferredSource = _deferredSource;
            _deferredSource = null;

            // Re-evaluate: now that device info is available, check if device-only
            if (ctx.IsDeviceOnly)
            {
                if (!_espEverSeen) _espEverSeen = true;
                if (!_espFinalExitSeen)
                {
                    _espFinalExitSeen = true;
                }

                _currentState = EnrollmentCompletionState.Completed;
                return new TransitionResult
                {
                    Transitioned = true,
                    PreviousState = prev,
                    NewState = _currentState,
                    ShouldEmitEnrollmentComplete = true,
                    CompletionSource = deferredSource ?? "device_setup_provisioning_complete",
                    ShouldPersistState = true,
                    SignalsToRecord = new List<string> { "self_deploying_esp_final_exit" }
                };
            }

            // Not device-only: revert to appropriate non-terminal state
            if (_espFinalExitSeen)
                _currentState = EnrollmentCompletionState.EspExitedAwaitingCompletion;
            else if (_espEverSeen)
                _currentState = _desktopArrived
                    ? EnrollmentCompletionState.DesktopArrivedEspBlocking
                    : EnrollmentCompletionState.EspActive;
            else
                _currentState = _desktopArrived
                    ? EnrollmentCompletionState.DesktopArrivedAwaitingHello
                    : EnrollmentCompletionState.Idle;

            return new TransitionResult
            {
                Transitioned = true,
                PreviousState = prev,
                NewState = _currentState,
                DeferredSource = deferredSource
            };
        }

        // ===== Guard Helpers (called under lock) =====

        /// <summary>
        /// Checks all three completion guards for a given source.
        /// </summary>
        private bool CanComplete(CompletionContext ctx, string source)
        {
            bool helloResolved = CompletionGuards.IsHelloResolved(
                ctx.HasHelloTracker, ctx.IsHelloCompleted,
                ctx.IsHelloPolicyConfigured, ctx.IsDeviceOnly);

            bool espGateBlocking = CompletionGuards.IsEspGateBlocking(
                source, ctx.EnrollmentType, _espEverSeen, _espFinalExitSeen);

            bool hybridRebootGateBlocking = CompletionGuards.IsHybridRebootGateBlocking(
                ctx.IsHybridJoin, source,
                ctx.ImePatternSeenUtc, ctx.EspFinalExitUtc, ctx.AgentStartTimeUtc);

            return helloResolved && !espGateBlocking && !hybridRebootGateBlocking;
        }

        /// <summary>
        /// Completion check with Hello always resolved (caller has just confirmed Hello completed).
        /// Only checks ESP gate and hybrid reboot gate.
        /// </summary>
        private bool CanCompleteWithHelloResolved(CompletionContext ctx, string source)
        {
            bool espGateBlocking = CompletionGuards.IsEspGateBlocking(
                source, ctx.EnrollmentType, _espEverSeen, _espFinalExitSeen);

            bool hybridRebootGateBlocking = CompletionGuards.IsHybridRebootGateBlocking(
                ctx.IsHybridJoin, source,
                ctx.ImePatternSeenUtc, ctx.EspFinalExitUtc, ctx.AgentStartTimeUtc);

            return !espGateBlocking && !hybridRebootGateBlocking;
        }
    }
}
