using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.Core.Monitoring.Flows
{
    public sealed class DevicePreparationFlow : IEnrollmentFlowHandler
    {
        public EnrollmentType SupportedType => EnrollmentType.DevicePreparation;
        public bool TracksEspPhases => false;
        public bool AppliesEspGateOnDesktopArrival => false;
    }
}
