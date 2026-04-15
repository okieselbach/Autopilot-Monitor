namespace AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Completion
{
    /// <summary>
    /// Explicit states for the enrollment completion state machine.
    /// Each state represents a distinguishable configuration of the implicit boolean flags
    /// that previously existed only as field combinations in EnrollmentTracker.CompletionLogic.cs.
    /// </summary>
    internal enum EnrollmentCompletionState
    {
        /// <summary>
        /// Initial state. No completion-relevant signals received.
        /// Waiting for: ESP phase event, desktop arrival, or device setup provisioning.
        /// </summary>
        Idle,

        /// <summary>
        /// ESP has been seen (at least one phase detected) but no final exit yet.
        /// Waiting for: ESP exit (FinalizingSetup trigger), desktop arrival, or device-only timer.
        /// </summary>
        EspActive,

        /// <summary>
        /// ESP exited from AccountSetup (or desktop-arrival backup). espFinalExitSeen=true.
        /// Waiting for: Hello resolution, IME user session completion, or safety timeout.
        /// </summary>
        EspExitedAwaitingCompletion,

        /// <summary>
        /// IME user session completed but Hello is configured and not yet resolved.
        /// isWaitingForHello=true. Safety timer running (420s / 7 min).
        /// </summary>
        WaitingForHello,

        /// <summary>
        /// IME user session completed but ESP provisioning categories not yet resolved in registry.
        /// isWaitingForEspSettle=true. Settle timer running (30s).
        /// </summary>
        WaitingForEspSettle,

        /// <summary>
        /// Desktop arrived but ESP is still active (ESP gate blocking completion).
        /// Waiting for: ESP final exit.
        /// </summary>
        DesktopArrivedEspBlocking,

        /// <summary>
        /// Desktop arrived, no ESP blocking (ESP exited, never seen, or v2). Hello may be pending.
        /// Waiting for: Hello resolution or direct completion.
        /// </summary>
        DesktopArrivedAwaitingHello,

        /// <summary>
        /// Device-only deployment detected (Self-Deploying or SkipUserStatusPage=true without AAD user).
        /// Waiting for provisioning complete or IME completion. Hello guard bypassed.
        /// </summary>
        DeviceOnlyAwaitingCompletion,

        /// <summary>
        /// Device-only ESP exit detected but completion didn't fire immediately (Hello pending).
        /// Safety timer running (420s / 7 min).
        /// </summary>
        DeviceOnlySafetyWait,

        /// <summary>
        /// Hybrid join: esp_hello_composite blocked by reboot gate.
        /// Waiting for: IME user session completion or agent restart confirmation after ESP exit.
        /// </summary>
        HybridRebootGateBlocked,

        /// <summary>
        /// Device info not yet collected when a completion signal arrived.
        /// Completion source deferred until CollectDeviceInfo finishes (pendingCompletionSource set).
        /// </summary>
        DeferredForDeviceInfo,

        /// <summary>
        /// Terminal: enrollment_complete event emitted. No further transitions possible.
        /// </summary>
        Completed,

        /// <summary>
        /// Terminal: enrollment_failed event emitted. No further transitions possible.
        /// </summary>
        Failed,

        /// <summary>
        /// Terminal: whiteglove_complete event emitted. No further transitions possible.
        /// </summary>
        WhiteGloveCompleted
    }

    /// <summary>
    /// Extension methods for EnrollmentCompletionState.
    /// </summary>
    internal static class EnrollmentCompletionStateExtensions
    {
        /// <summary>
        /// Returns true if the state is terminal (Completed, Failed, or WhiteGloveCompleted).
        /// No further transitions are allowed from terminal states.
        /// </summary>
        public static bool IsTerminal(this EnrollmentCompletionState state)
        {
            return state == EnrollmentCompletionState.Completed
                || state == EnrollmentCompletionState.Failed
                || state == EnrollmentCompletionState.WhiteGloveCompleted;
        }

        /// <summary>
        /// Returns true if the state is a waiting state (actively waiting for a signal or timeout).
        /// </summary>
        public static bool IsWaiting(this EnrollmentCompletionState state)
        {
            return state == EnrollmentCompletionState.WaitingForHello
                || state == EnrollmentCompletionState.WaitingForEspSettle
                || state == EnrollmentCompletionState.DeviceOnlySafetyWait
                || state == EnrollmentCompletionState.HybridRebootGateBlocked
                || state == EnrollmentCompletionState.DeferredForDeviceInfo;
        }
    }
}
