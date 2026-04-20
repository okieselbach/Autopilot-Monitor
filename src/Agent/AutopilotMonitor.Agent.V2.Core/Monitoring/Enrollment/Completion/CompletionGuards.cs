using System;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Completion
{
    /// <summary>
    /// Pure-function guards for enrollment completion decisions.
    /// Extracted 1:1 from the lock block in TryEmitEnrollmentComplete() for testability.
    /// Each method mirrors the exact condition evaluated in CompletionLogic.cs.
    /// </summary>
    internal static class CompletionGuards
    {
        /// <summary>
        /// Guard 1: Hello resolution check.
        /// Hello is considered resolved when:
        ///   - No EspAndHelloTracker exists (no Hello tracking)
        ///   - Hello has already completed (success, skip, or timeout)
        ///   - Hello policy is not configured (Hello disabled)
        ///   - Deployment is device-only (no interactive user session)
        /// </summary>
        /// <remarks>
        /// Source: EnrollmentTracker.CompletionLogic.cs, TryEmitEnrollmentComplete(),
        /// line: helloResolved = _espAndHelloTracker == null || ...IsHelloCompleted || !...IsPolicyConfigured || isDeviceOnly
        /// </remarks>
        public static bool IsHelloResolved(bool hasTracker, bool isHelloCompleted,
            bool isPolicyConfigured, bool isDeviceOnly)
        {
            return !hasTracker
                || isHelloCompleted
                || !isPolicyConfigured
                || isDeviceOnly;
        }

        /// <summary>
        /// Guard 2: ESP gate for desktop-based completion sources.
        /// Blocks completion when:
        ///   - Source is desktop_arrival or desktop_hello
        ///   - Enrollment type is v1 (not WDP)
        ///   - ESP has been seen but has NOT exited yet
        /// Rationale: In v1 enrollments with active ESP, desktop presence alone is
        /// NOT sufficient to complete (AccountSetup may still be running).
        /// </summary>
        /// <remarks>
        /// Source: EnrollmentTracker.CompletionLogic.cs, TryEmitEnrollmentComplete(),
        /// line: espGateBlocked = (source == "desktop_arrival" || source == "desktop_hello") &amp;&amp; enrollmentType != "v2" &amp;&amp; espEverSeen &amp;&amp; !espFinalExitSeen
        /// </remarks>
        public static bool IsEspGateBlocking(string source, string enrollmentType,
            bool espEverSeen, bool espFinalExitSeen)
        {
            return (source == "desktop_arrival" || source == "desktop_hello")
                && enrollmentType != "v2"
                && espEverSeen
                && !espFinalExitSeen;
        }

        /// <summary>
        /// Guard 3: Hybrid join reboot gate.
        /// In hybrid join, ESP may exit for a mid-enrollment reboot (domain user login required).
        /// Blocks esp_hello_composite unless we have stronger confirmation:
        ///   - IME user session completed (imePatternSeenUtc has value)
        ///   - Agent restarted after ESP exit (agentStartTimeUtc > espFinalExitUtc)
        /// </summary>
        /// <remarks>
        /// Source: EnrollmentTracker.CompletionLogic.cs, TryEmitEnrollmentComplete(),
        /// lines: if (_isHybridJoin &amp;&amp; source == "esp_hello_composite") { ... hybridRebootGateBlocked = !hasImeCompletion &amp;&amp; !agentRestartedAfterEspExit; }
        /// </remarks>
        public static bool IsHybridRebootGateBlocking(bool isHybridJoin, string source,
            DateTime? imePatternSeenUtc, DateTime? espFinalExitUtc, DateTime agentStartTimeUtc)
        {
            if (!isHybridJoin || source != "esp_hello_composite")
                return false;

            bool hasImeCompletion = imePatternSeenUtc.HasValue;
            bool agentRestartedAfterEspExit = espFinalExitUtc.HasValue
                && agentStartTimeUtc > espFinalExitUtc.Value;

            return !hasImeCompletion && !agentRestartedAfterEspExit;
        }

        /// <summary>
        /// Deployment classification: device-only check.
        /// A deployment is device-only when:
        ///   - Self-Deploying mode (autopilotMode == 1), OR
        ///   - SkipUserStatusPage == true AND no user has AAD-joined
        /// </summary>
        /// <remarks>
        /// Source: EnrollmentTracker.cs, property IsDeviceOnlyDeployment
        /// line: IsSelfDeploying || (_skipUserStatusPage == true &amp;&amp; !_aadJoinedWithUser)
        /// </remarks>
        public static bool IsDeviceOnlyDeployment(int? autopilotMode, bool? skipUserStatusPage,
            bool aadJoinedWithUser)
        {
            bool isSelfDeploying = autopilotMode == 1;
            return isSelfDeploying || (skipUserStatusPage == true && !aadJoinedWithUser);
        }
    }
}
