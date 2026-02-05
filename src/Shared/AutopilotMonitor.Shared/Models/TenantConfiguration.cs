using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Tenant-specific configuration stored in Azure Table Storage
    /// PartitionKey = TenantId
    /// RowKey = "config"
    /// </summary>
    public class TenantConfiguration
    {
        /// <summary>
        /// Tenant ID (PartitionKey in Table Storage)
        /// </summary>
        public string TenantId { get; set; }

        /// <summary>
        /// When the configuration was last updated
        /// </summary>
        public DateTime LastUpdated { get; set; }

        /// <summary>
        /// Updated by (user email or system)
        /// </summary>
        public string UpdatedBy { get; set; }

        // ===== SECURITY SETTINGS =====

        /// <summary>
        /// Whether request security validation is enabled
        /// </summary>
        public bool SecurityEnabled { get; set; } = true;

        /// <summary>
        /// Rate limit: Maximum requests per minute per device
        /// Default: 100
        /// </summary>
        public int RateLimitRequestsPerMinute { get; set; } = 100;

        /// <summary>
        /// Hardware whitelist: Allowed manufacturers (supports wildcards like "Dell*")
        /// Comma-separated list
        /// </summary>
        public string ManufacturerWhitelist { get; set; } = "Dell*,HP*,Lenovo*,Microsoft Corporation";

        /// <summary>
        /// Hardware whitelist: Allowed models (supports wildcards like "Latitude*")
        /// Comma-separated list
        /// Default: "*" (all models allowed)
        /// </summary>
        public string ModelWhitelist { get; set; } = "*";

        /// <summary>
        /// Whether to validate serial numbers against Intune Autopilot
        /// Requires Graph API integration
        /// </summary>
        public bool ValidateSerialNumber { get; set; } = false;

        // ===== DATA MANAGEMENT SETTINGS =====

        /// <summary>
        /// Data retention period in days
        /// Sessions and events older than this will be deleted by the daily maintenance job
        /// Default: 90 days
        /// </summary>
        public int DataRetentionDays { get; set; } = 90;

        /// <summary>
        /// Session timeout in hours
        /// Sessions in "InProgress" status longer than this will be marked as "Failed - Timed Out"
        /// This prevents stalled sessions from running indefinitely and skewing statistics
        /// Recommended: Use the same value as your ESP (Enrollment Status Page) timeout
        /// Default: 5 hours
        /// </summary>
        public int SessionTimeoutHours { get; set; } = 5;

        // ===== FUTURE SETTINGS (Extensible) =====

        /// <summary>
        /// Custom settings as JSON (for future extensibility)
        /// </summary>
        public string CustomSettings { get; set; }

        // ===== HELPER METHODS =====

        /// <summary>
        /// Gets manufacturer whitelist as array
        /// </summary>
        public string[] GetManufacturerWhitelist()
        {
            if (string.IsNullOrEmpty(ManufacturerWhitelist))
                return new[] { "*" };

            return ManufacturerWhitelist.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>
        /// Gets model whitelist as array
        /// </summary>
        public string[] GetModelWhitelist()
        {
            if (string.IsNullOrEmpty(ModelWhitelist))
                return new[] { "*" };

            return ModelWhitelist.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>
        /// Creates default configuration for a tenant
        /// </summary>
        public static TenantConfiguration CreateDefault(string tenantId)
        {
            return new TenantConfiguration
            {
                TenantId = tenantId,
                LastUpdated = DateTime.UtcNow,
                UpdatedBy = "System",
                SecurityEnabled = true,
                RateLimitRequestsPerMinute = 100,
                ManufacturerWhitelist = "Dell*,HP*,Lenovo*,Microsoft Corporation",
                ModelWhitelist = "*",
                ValidateSerialNumber = false,
                DataRetentionDays = 90,
                SessionTimeoutHours = 5
            };
        }
    }
}
