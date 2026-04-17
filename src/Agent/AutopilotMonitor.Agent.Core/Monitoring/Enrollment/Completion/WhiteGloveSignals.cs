using System;

namespace AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Completion
{
    /// <summary>
    /// Snapshot of all signals relevant for WhiteGlove (Part 1) classification.
    /// Populated under _stateLock in the caller and consumed outside the lock by
    /// <see cref="WhiteGloveClassifier.Classify"/> — mirrors the CompletionContext
    /// pattern to preserve the existing thread-safety guarantees.
    ///
    /// Source-of-truth is the EnrollmentTracker state. When the tracker uses
    /// CompletionStateMachine (shadow), use <see cref="FromContext"/> to keep both
    /// decision paths in sync.
    /// </summary>
    internal sealed class WhiteGloveSignals
    {
        // "Hard" positive signals (registry / Shell-Core)
        public bool ShellCoreWhiteGloveSuccess { get; set; }     // Event 62407 "WhiteGlove_Success" — strongest signal
        public bool HasSaveWhiteGloveSuccessResult { get; set; } // DeviceSetup registry subcategory — also fires on non-WG
        public bool IsWhiteGloveStartDetected { get; set; }      // Event 509 — soft, fires on hybrid too
        public bool IsFooUserDetected { get; set; }              // foouser@/autopilot@ in JoinInfo — pre-provisioning indicator
        public bool AgentRestartedAfterEspExit { get; set; }     // typical WG Part 1 boundary

        // Hard excluders (drive score negative)
        public bool AadJoinedWithUser { get; set; }              // real user signed in — not WG Part 1 anymore
        public bool DesktopArrived { get; set; }                 // explorer.exe running — not WG Part 1 anymore
        public bool HasAccountSetupActivity { get; set; }        // user-driven ESP category active

        // Deployment context
        public bool IsDeviceOnlyDeployment { get; set; }

        // Optional metadata (populated for observability, not scored)
        public DateTime? EspFinalExitUtc { get; set; }
        public DateTime? DeviceSetupProvisioningCompleteUtc { get; set; }

        /// <summary>
        /// Builds a signal snapshot from a <see cref="CompletionContext"/>. Used by the
        /// CompletionStateMachine so that state-machine decisions use the exact same
        /// inputs as the EnrollmentTracker's direct decisions.
        /// </summary>
        public static WhiteGloveSignals FromContext(CompletionContext ctx)
        {
            if (ctx == null) throw new ArgumentNullException(nameof(ctx));

            return new WhiteGloveSignals
            {
                ShellCoreWhiteGloveSuccess = ctx.ShellCoreWhiteGloveSuccess,
                HasSaveWhiteGloveSuccessResult = ctx.HasSaveWhiteGloveSuccessResult,
                IsWhiteGloveStartDetected = ctx.WhiteGloveStartDetected,
                IsFooUserDetected = ctx.IsFooUserDetected,
                AgentRestartedAfterEspExit = ctx.AgentRestartedAfterEspExit,

                AadJoinedWithUser = ctx.AadJoinedWithUser,
                DesktopArrived = ctx.DesktopArrived,
                HasAccountSetupActivity = ctx.HasAccountSetupActivity,

                IsDeviceOnlyDeployment = ctx.IsDeviceOnly,

                EspFinalExitUtc = ctx.EspFinalExitUtc,
            };
        }
    }
}
