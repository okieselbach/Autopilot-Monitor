using System;
using System.Collections.Generic;
using System.Linq;

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
        /// Domain name extracted from the first user's UPN
        /// Used for display purposes (e.g., contoso.com)
        /// </summary>
        public string DomainName { get; set; }

        /// <summary>
        /// When the configuration was last updated
        /// </summary>
        public DateTime LastUpdated { get; set; }

        /// <summary>
        /// Updated by (user email or system)
        /// </summary>
        public string UpdatedBy { get; set; }

        // ===== TENANT STATUS =====

        /// <summary>
        /// Whether this tenant is disabled/suspended
        /// If true, users from this tenant cannot log in
        /// Default: false
        /// </summary>
        public bool Disabled { get; set; } = false;

        /// <summary>
        /// Optional reason why the tenant was disabled
        /// Displayed to users attempting to log in
        /// </summary>
        public string DisabledReason { get; set; }

        /// <summary>
        /// Optional date/time until which the tenant is disabled
        /// If set and in the past, the tenant can be automatically re-enabled
        /// If null, the tenant remains disabled until manually re-enabled
        /// </summary>
        public DateTime? DisabledUntil { get; set; }

        // ===== SECURITY SETTINGS =====

        /// <summary>
        /// Rate limit: Maximum requests per minute per device
        /// This value is synchronized from the global AdminConfiguration
        /// Default: 100
        /// </summary>
        public int RateLimitRequestsPerMinute { get; set; } = 100;

        /// <summary>
        /// Optional custom rate limit for this tenant (overrides RateLimitRequestsPerMinute)
        /// If set (not null), this custom value takes precedence over the global default
        /// Note: This is only configurable by Galactic Admins directly in the database
        /// </summary>
        public int? CustomRateLimitRequestsPerMinute { get; set; } = null;

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

        /// <summary>
        /// Emergency bypass for agent security gate (Galactic Admin use only).
        /// If true, agent requests are accepted even when ValidateSerialNumber is false.
        /// Default: false
        /// </summary>
        public bool AllowInsecureAgentRequests { get; set; } = false;

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

        // ===== PAYLOAD SETTINGS =====

        /// <summary>
        /// Maximum decompressed NDJSON payload size in MB
        /// Protects against memory exhaustion and denial-of-service attacks
        /// Default: 5 MB
        /// </summary>
        public int MaxNdjsonPayloadSizeMB { get; set; } = 5;

        // ===== AGENT COLLECTOR SETTINGS =====

        /// <summary>
        /// Enable Performance Collector (CPU, memory, disk, network monitoring)
        /// Generates ~1 event per interval - can create significant traffic
        /// Default: false (opt-in)
        /// </summary>
        public bool EnablePerformanceCollector { get; set; } = false;

        /// <summary>
        /// Performance collector interval in seconds
        /// Default: 60 seconds
        /// </summary>
        public int PerformanceCollectorIntervalSeconds { get; set; } = 60;

        // ===== AGENT AUTH CIRCUIT BREAKER =====

        /// <summary>
        /// Maximum consecutive authentication failures (401/403) before the agent shuts down.
        /// null = use default (5). 0 = disabled (retry forever).
        /// </summary>
        public int? MaxAuthFailures { get; set; }

        /// <summary>
        /// Maximum time in minutes the agent keeps retrying after the first auth failure.
        /// null = use default (0 = disabled, only MaxAuthFailures applies).
        /// </summary>
        public int? AuthFailureTimeoutMinutes { get; set; }

        // ===== HELPER METHODS =====

        /// <summary>
        /// Checks if the tenant is currently disabled
        /// Takes into account DisabledUntil if set
        /// </summary>
        public bool IsCurrentlyDisabled()
        {
            if (!Disabled)
                return false;

            // If DisabledUntil is set and in the past, tenant is no longer disabled
            if (DisabledUntil.HasValue && DisabledUntil.Value <= DateTime.UtcNow)
                return false;

            return true;
        }

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
                DomainName = "",
                LastUpdated = DateTime.UtcNow,
                UpdatedBy = "System",
                Disabled = false,
                DisabledReason = null,
                DisabledUntil = null,
                RateLimitRequestsPerMinute = 100,
                CustomRateLimitRequestsPerMinute = null,
                ManufacturerWhitelist = "Dell*,HP*,Lenovo*,Microsoft Corporation",
                ModelWhitelist = "*",
                ValidateSerialNumber = false,
                AllowInsecureAgentRequests = false,
                DataRetentionDays = 90,
                SessionTimeoutHours = 5,
                MaxNdjsonPayloadSizeMB = 5,
                EnablePerformanceCollector = false,
                PerformanceCollectorIntervalSeconds = 60
            };
        }
    }
}
