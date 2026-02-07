using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Pre-computed platform-wide statistics for the public landing page.
    /// Stored as a single row (PartitionKey: "global", RowKey: "current").
    /// Recomputed during daily maintenance; incremented during registration/login.
    /// </summary>
    public class PlatformStats
    {
        /// <summary>Total enrollment sessions monitored since launch</summary>
        public long TotalEnrollments { get; set; }

        /// <summary>Total unique users who logged in</summary>
        public long TotalUsers { get; set; }

        /// <summary>Total unique tenants using the platform</summary>
        public long TotalTenants { get; set; }

        /// <summary>Total unique device models seen (manufacturer + model)</summary>
        public long UniqueDeviceModels { get; set; }

        /// <summary>Total events processed across all tenants</summary>
        public long TotalEventsProcessed { get; set; }

        /// <summary>Total successful enrollments</summary>
        public long SuccessfulEnrollments { get; set; }

        /// <summary>Total analysis issues detected</summary>
        public long IssuesDetected { get; set; }

        /// <summary>When these stats were last fully recomputed</summary>
        public DateTime LastFullCompute { get; set; }

        /// <summary>When these stats were last updated (including incremental)</summary>
        public DateTime LastUpdated { get; set; }
    }
}
