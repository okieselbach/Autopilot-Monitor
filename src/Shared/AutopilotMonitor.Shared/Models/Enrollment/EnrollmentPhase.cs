namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Represents the phases of Autopilot enrollment
    /// </summary>
    public enum EnrollmentPhase
    {
        /// <summary>
        /// Unknown: Events without explicit phase assignment (sorted chronologically into active phase)
        /// </summary>
        Unknown = -1,

        /// <summary>
        /// Start: Initial boot, OOBE begins
        /// </summary>
        Start = 0,

        /// <summary>
        /// Device Preparation: OOBE, network, identity, Autopilot profile applied
        /// </summary>
        DevicePreparation = 1,

        /// <summary>
        /// Device Setup: ESP Device phase, agent deployed/bootstrapped, policies applied
        /// </summary>
        DeviceSetup = 2,

        /// <summary>
        /// Apps (Device): Required apps installing during device phase
        /// </summary>
        AppsDevice = 3,

        /// <summary>
        /// Account Setup: ESP User/Account phase, user sign-in
        /// </summary>
        AccountSetup = 4,

        /// <summary>
        /// Apps (User): User-targeted apps installing
        /// </summary>
        AppsUser = 5,

        /// <summary>
        /// Finalizing Setup: All user apps completed, waiting for Windows Hello provisioning or final ESP steps
        /// </summary>
        FinalizingSetup = 6,

        /// <summary>
        /// Complete: Desktop reached, enrollment succeeded
        /// </summary>
        Complete = 7,

        /// <summary>
        /// Failed: Enrollment failed
        /// </summary>
        Failed = 99
    }
}
