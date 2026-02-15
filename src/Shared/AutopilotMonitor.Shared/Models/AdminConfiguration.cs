using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Global platform configuration managed by Galactic Admins
    /// Stored in Azure Table Storage with single instance
    /// PartitionKey = "GlobalConfig"
    /// RowKey = "config"
    /// </summary>
    public class AdminConfiguration
    {
        /// <summary>
        /// Partition key (always "GlobalConfig")
        /// </summary>
        public string PartitionKey { get; set; } = "GlobalConfig";

        /// <summary>
        /// Row key (always "config")
        /// </summary>
        public string RowKey { get; set; } = "config";

        /// <summary>
        /// When the configuration was last updated
        /// </summary>
        public DateTime LastUpdated { get; set; }

        /// <summary>
        /// Updated by (Galactic Admin user email)
        /// </summary>
        public string UpdatedBy { get; set; }

        // ===== RATE LIMITING SETTINGS =====

        /// <summary>
        /// Global default rate limit: Maximum requests per minute per device
        /// This applies to all tenants unless they have a custom override
        /// Default: 100
        /// </summary>
        public int GlobalRateLimitRequestsPerMinute { get; set; } = 100;

        /// <summary>
        /// Creates default configuration
        /// </summary>
        public static AdminConfiguration CreateDefault()
        {
            return new AdminConfiguration
            {
                PartitionKey = "GlobalConfig",
                RowKey = "config",
                LastUpdated = DateTime.UtcNow,
                UpdatedBy = "System",
                GlobalRateLimitRequestsPerMinute = 100
            };
        }
    }
}
