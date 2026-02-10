using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Registration payload for a new enrollment session
    /// </summary>
    public class SessionRegistration
    {
        /// <summary>
        /// Unique session identifier (GUID)
        /// </summary>
        public string SessionId { get; set; }

        /// <summary>
        /// Tenant identifier
        /// </summary>
        public string TenantId { get; set; }

        /// <summary>
        /// Device serial number
        /// </summary>
        public string SerialNumber { get; set; }

        /// <summary>
        /// Device manufacturer
        /// </summary>
        public string Manufacturer { get; set; }

        /// <summary>
        /// Device model
        /// </summary>
        public string Model { get; set; }

        /// <summary>
        /// Device name
        /// </summary>
        public string DeviceName { get; set; }

        /// <summary>
        /// OS build number
        /// </summary>
        public string OsBuild { get; set; }

        /// <summary>
        /// OS edition (e.g., "Pro", "Enterprise")
        /// </summary>
        public string OsEdition { get; set; }

        /// <summary>
        /// OS language
        /// </summary>
        public string OsLanguage { get; set; }

        /// <summary>
        /// Autopilot profile name (if detectable)
        /// </summary>
        public string AutopilotProfileName { get; set; }

        /// <summary>
        /// Autopilot profile ID (if detectable)
        /// </summary>
        public string AutopilotProfileId { get; set; }

        /// <summary>
        /// Whether this is user-driven enrollment
        /// </summary>
        public bool IsUserDriven { get; set; }

        /// <summary>
        /// Whether this is pre-provisioned
        /// </summary>
        public bool IsPreProvisioned { get; set; }

        /// <summary>
        /// Timestamp when enrollment started (UTC)
        /// </summary>
        public DateTime StartedAt { get; set; }

        /// <summary>
        /// Agent version
        /// </summary>
        public string AgentVersion { get; set; }

        /// <summary>
        /// Enrollment type detected by the agent: "v1" (Autopilot Classic/ESP) or "v2" (Windows Device Preparation)
        /// </summary>
        public string EnrollmentType { get; set; } = "v1";

        public SessionRegistration()
        {
            SessionId = Guid.NewGuid().ToString();
            StartedAt = DateTime.UtcNow;
        }
    }
}
