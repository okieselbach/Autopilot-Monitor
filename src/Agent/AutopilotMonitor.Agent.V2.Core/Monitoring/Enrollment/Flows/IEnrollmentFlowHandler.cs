using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Flows
{
    /// <summary>
    /// Encapsulates the policy decisions that differ between the Classic Autopilot (v1)
    /// flow and the Windows Autopilot Device Preparation (v2) flow.
    ///
    /// Today the flow handler only owns policy bits; tracker ownership remains in
    /// <c>CollectorCoordinator</c>. When DevPrep-specific trackers land, they will
    /// move into the corresponding flow handler.
    /// </summary>
    public interface IEnrollmentFlowHandler
    {
        EnrollmentType SupportedType { get; }

        /// <summary>
        /// True when the flow relies on ESP-phase signals for progress and completion.
        /// Classic: true. DevPrep (WDP): false (no ESP exists).
        /// </summary>
        bool TracksEspPhases { get; }

        /// <summary>
        /// True when completion triggered by desktop-arrival / desktop-hello must be
        /// blocked while ESP has been observed but has not reached its final exit.
        /// Classic: true (AccountSetup may still be running). DevPrep: false.
        /// </summary>
        bool AppliesEspGateOnDesktopArrival { get; }
    }
}
