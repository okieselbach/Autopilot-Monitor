using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Flows
{
    public sealed class ClassicAutopilotFlow : IEnrollmentFlowHandler
    {
        public EnrollmentType SupportedType => EnrollmentType.Classic;
        public bool TracksEspPhases => true;
        public bool AppliesEspGateOnDesktopArrival => true;
    }
}
