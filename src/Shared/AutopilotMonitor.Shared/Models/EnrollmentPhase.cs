namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Represents the phases of Autopilot enrollment
    /// </summary>
    public enum EnrollmentPhase
    {
        /// <summary>
        /// Pre-flight: Agent deployed, session registered
        /// </summary>
        PreFlight = 0,

        /// <summary>
        /// OOBE/Network: Network connection established
        /// </summary>
        Network = 1,

        /// <summary>
        /// Identity: Azure AD/Entra joined, device certificate issued
        /// </summary>
        Identity = 2,

        /// <summary>
        /// MDM Enrollment: Intune enrolled, policies starting
        /// </summary>
        MdmEnrollment = 3,

        /// <summary>
        /// ESP Device Setup: Device policies applied
        /// </summary>
        EspDeviceSetup = 4,

        /// <summary>
        /// App Installation: Required apps installing
        /// </summary>
        AppInstallation = 5,

        /// <summary>
        /// ESP User Setup: User policies and apps
        /// </summary>
        EspUserSetup = 6,

        /// <summary>
        /// Complete: Desktop reached
        /// </summary>
        Complete = 7,

        /// <summary>
        /// Failed: Enrollment failed
        /// </summary>
        Failed = 99
    }
}
