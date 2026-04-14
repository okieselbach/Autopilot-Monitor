using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.Core.Monitoring.Flows
{
    public static class EnrollmentFlowFactory
    {
        /// <summary>
        /// Returns the flow handler for the given enrollment type.
        /// <see cref="EnrollmentType.Unknown"/> falls back to the Classic flow
        /// (safe default — Classic is the long-standing behavior).
        /// </summary>
        public static IEnrollmentFlowHandler Create(EnrollmentType type) => type switch
        {
            EnrollmentType.DevicePreparation => new DevicePreparationFlow(),
            _ => new ClassicAutopilotFlow()
        };

        public static IEnrollmentFlowHandler FromWireFormat(string wireFormat)
            => Create(EnrollmentTypeExtensions.FromWireFormat(wireFormat));
    }
}
