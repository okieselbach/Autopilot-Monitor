using System;
using System.Collections.Generic;
using System.Text.Json;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Global platform configuration managed by Global Admins
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
        /// Updated by (Global Admin user email)
        /// </summary>
        public string UpdatedBy { get; set; } = default!;

        // ===== RATE LIMITING SETTINGS =====

        /// <summary>
        /// Global default rate limit: Maximum requests per minute per device
        /// This applies to all tenants unless they have a custom override
        /// Default: 100
        /// </summary>
        public int GlobalRateLimitRequestsPerMinute { get; set; } = 100;

        /// <summary>
        /// Per-user rate limit for standard users (Tenant Admins, Operators, Viewers).
        /// Requests per minute keyed by UPN. Default: 120.
        /// </summary>
        public int UserRateLimitRequestsPerMinute { get; set; } = 120;

        /// <summary>
        /// Per-user rate limit for Global Admins.
        /// Higher budget but not exempt. Default: 600.
        /// </summary>
        public int GlobalAdminRateLimitRequestsPerMinute { get; set; } = 600;

        /// <summary>
        /// JSON-serialized plan tier definitions mapping tier name to rate limits and features.
        /// Example: {"free":{"apiRateLimit":60},"pro":{"apiRateLimit":300},"enterprise":{"apiRateLimit":1000}}
        /// </summary>
        public string? PlanTierDefinitionsJson { get; set; }

        /// <summary>
        /// Container SAS URL used by maintenance to publish platform stats JSON files.
        /// Expected format: https://{account}.blob.core.windows.net/{container}?sv=...&sig=...
        /// </summary>
        public string PlatformStatsBlobSasUrl { get; set; } = string.Empty;

        /// <summary>
        /// Idle timeout in minutes for periodic collectors (Performance, AgentSelfMetrics).
        /// When no real enrollment event (app install, ESP phase change, etc.) is detected
        /// within this window, collectors stop automatically to prevent session bloat.
        /// They restart automatically when new enrollment activity is detected.
        /// 0 = disabled (collectors run indefinitely). Default: 15 minutes.
        /// </summary>
        public int CollectorIdleTimeoutMinutes { get; set; } = 15;

        // ===== MAINTENANCE AUTO-BLOCK SETTINGS =====

        /// <summary>
        /// Max active data window in hours for maintenance auto-block detection.
        /// Sessions with LastEventAt within the last MaxSessionWindowHours AND StartedAt older
        /// than MaxSessionWindowHours will have their device blocked by the nightly maintenance function.
        /// 0 = disabled. Default: 24.
        /// </summary>
        public int MaxSessionWindowHours { get; set; } = 24;

        /// <summary>
        /// Duration in hours for maintenance-triggered device blocks (excessive data senders).
        /// Default: 12.
        /// </summary>
        public int MaintenanceBlockDurationHours { get; set; } = 12;

        /// <summary>
        /// Retention period in days for operational events in the OpsEvents table.
        /// Events older than this are deleted by the periodic maintenance job.
        /// 0 = no cleanup (not recommended). Default: 90 days.
        /// </summary>
        public int OpsEventRetentionDays { get; set; } = 90;

        // ===== FEEDBACK SETTINGS =====

        /// <summary>
        /// Global kill-switch for the in-app feedback prompt.
        /// When false, no user sees the feedback bubble regardless of other settings.
        /// Default: true.
        /// </summary>
        public bool FeedbackEnabled { get; set; } = true;

        /// <summary>
        /// Minimum tenant age in days before users are prompted for feedback.
        /// Prevents asking brand-new tenants who haven't had meaningful experience yet.
        /// Default: 14 days.
        /// </summary>
        public int FeedbackMinTenantAgeDays { get; set; } = 14;

        /// <summary>
        /// Cooldown in days after a user interacts with the feedback prompt
        /// before they are prompted again. 0 = never re-prompt (single wave only).
        /// Default: 60 days.
        /// </summary>
        public int FeedbackCooldownDays { get; set; } = 60;

        // ===== DIAGNOSTICS LOG PATHS =====

        /// <summary>
        /// JSON-serialized list of global diagnostics log paths/wildcards
        /// to include in the diagnostics ZIP package for all tenants.
        /// Each entry: { "path": "...", "description": "...", "isBuiltIn": true }
        /// </summary>
        public string DiagnosticsGlobalLogPathsJson { get; set; } = default!;

        /// <summary>
        /// Returns the deserialized list of global diagnostics log paths.
        /// </summary>
        public List<DiagnosticsLogPath> GetDiagnosticsGlobalLogPaths()
        {
            if (string.IsNullOrEmpty(DiagnosticsGlobalLogPathsJson))
                return new List<DiagnosticsLogPath>();
            try
            {
                return JsonSerializer.Deserialize<List<DiagnosticsLogPath>>(DiagnosticsGlobalLogPathsJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new List<DiagnosticsLogPath>();
            }
            catch
            {
                return new List<DiagnosticsLogPath>();
            }
        }

        // ===== DIAGNOSTICS DOWNLOAD LIMITS =====

        /// <summary>
        /// Maximum allowed diagnostics download size in MB.
        /// Blobs exceeding this are rejected before streaming (413).
        /// 0 = no limit. Default: 500 MB.
        /// </summary>
        public int MaxDiagnosticsDownloadSizeMB { get; set; } = 500;

        /// <summary>
        /// Timeout in seconds for the entire diagnostics download+stream operation.
        /// 0 = no timeout. Default: 120 seconds.
        /// </summary>
        public int DiagnosticsDownloadTimeoutSeconds { get; set; } = 120;

        // ===== MCP ACCESS CONTROL =====

        /// <summary>
        /// Controls who can access the remote MCP server.
        /// "Disabled" = MCP off, "WhitelistOnly" = GlobalAdmins + McpUsers table (default),
        /// "AllMembers" = any authenticated user.
        /// </summary>
        public string McpAccessPolicy { get; set; } = nameof(Models.McpAccessPolicy.WhitelistOnly);

        // ===== VULNERABILITY CORRELATION SETTINGS =====

        /// <summary>
        /// NVD API key for higher rate limits (50 req/30s vs 5 req/30s without key).
        /// Free registration at https://nvd.nist.gov/developers/request-an-api-key
        /// null = operate without API key (slower, still functional).
        /// </summary>
        public string NvdApiKey { get; set; } = default!;

        // ===== OPS EVENT ALERTING =====

        /// <summary>
        /// JSON-serialized list of OpsAlertRule objects defining which event types
        /// trigger notifications. Provider-agnostic — rules apply to all enabled providers.
        /// </summary>
        public string OpsAlertRulesJson { get; set; } = default!;

        /// <summary>
        /// Returns the deserialized list of ops alert rules.
        /// </summary>
        public List<OpsAlertRule> GetOpsAlertRules()
        {
            if (string.IsNullOrEmpty(OpsAlertRulesJson))
                return new List<OpsAlertRule>();
            try
            {
                return JsonSerializer.Deserialize<List<OpsAlertRule>>(OpsAlertRulesJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new List<OpsAlertRule>();
            }
            catch
            {
                return new List<OpsAlertRule>();
            }
        }

        /// <summary>Whether the Telegram alert provider is enabled. Default: false.</summary>
        public bool OpsAlertTelegramEnabled { get; set; }

        /// <summary>Telegram chat ID for ops alerts (e.g. ITEngineer channel).</summary>
        public string OpsAlertTelegramChatId { get; set; } = default!;

        /// <summary>Whether the Teams alert provider is enabled. Default: false.</summary>
        public bool OpsAlertTeamsEnabled { get; set; }

        /// <summary>Teams Workflow webhook URL for ops alerts.</summary>
        public string OpsAlertTeamsWebhookUrl { get; set; } = default!;

        /// <summary>Whether the Slack alert provider is enabled. Default: false.</summary>
        public bool OpsAlertSlackEnabled { get; set; }

        /// <summary>Slack Incoming Webhook URL for ops alerts.</summary>
        public string OpsAlertSlackWebhookUrl { get; set; } = default!;

        // ===== AGENT INTEGRITY SETTINGS =====

        /// <summary>
        /// Version string of the latest published agent build (e.g. "1.0.706").
        /// Written by CI/CD pipeline and PS build scripts after agent ZIP upload.
        /// </summary>
        public string LatestAgentVersion { get; set; } = default!;

        /// <summary>
        /// SHA-256 hash (lowercase hex) of the latest published agent ZIP.
        /// Used as a backend hash-oracle: the agent compares this against the
        /// downloaded ZIP to verify integrity via a separate trust channel.
        /// Written by CI/CD pipeline and PS build scripts after agent ZIP upload.
        /// </summary>
        public string LatestAgentSha256 { get; set; } = default!;

        /// <summary>
        /// Whether vulnerability correlation is globally enabled.
        /// When false, agents still collect inventory but backend skips correlation.
        /// Default: true
        /// </summary>
        public bool VulnerabilityCorrelationEnabled { get; set; } = true;

        /// <summary>
        /// Last successful vulnerability data sync timestamp (UTC ISO 8601).
        /// Updated by VulnerabilityDataSyncFunction.
        /// </summary>
        public string VulnerabilityDataLastSyncUtc { get; set; } = default!;

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
                GlobalRateLimitRequestsPerMinute = 100,
                UserRateLimitRequestsPerMinute = 120,
                GlobalAdminRateLimitRequestsPerMinute = 600,
                PlatformStatsBlobSasUrl = string.Empty,
                CollectorIdleTimeoutMinutes = 15,
                MaxSessionWindowHours = 24,
                MaintenanceBlockDurationHours = 12
            };
        }
    }
}
