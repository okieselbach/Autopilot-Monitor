using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.Core.Monitoring.Flows
{
    public sealed class ClassicAutopilotFlow : IEnrollmentFlowHandler
    {
        public EnrollmentType SupportedType => EnrollmentType.Classic;
        public bool TracksEspPhases => true;
        public bool AppliesEspGateOnDesktopArrival => true;
    }
}
