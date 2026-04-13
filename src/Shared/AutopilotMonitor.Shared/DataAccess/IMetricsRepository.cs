using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Repository for usage metrics, platform stats, and user activity tracking.
    /// Covers: UsageMetrics, PlatformStats, UserActivity, AppInstallSummaries tables.
    /// </summary>
    public interface IMetricsRepository
    {
        // --- Usage Metrics Snapshots ---
        Task<bool> SaveUsageMetricsSnapshotAsync(UsageMetricsSnapshot metrics);
        Task<List<UsageMetricsSnapshot>> GetUsageMetricsSnapshotAsync(
            string? tenantId = null, string? startDate = null, string? endDate = null, int maxResults = 100);
        Task<bool> HasUsageMetricsSnapshotAsync(string date);

        // --- App Install Summaries ---
        Task<bool> StoreAppInstallSummaryAsync(AppInstallSummary summary);
        Task<List<AppInstallSummary>> GetAppInstallSummariesByTenantAsync(string tenantId);
        Task<List<AppInstallSummary>> GetAllAppInstallSummariesAsync();

        // --- Platform Stats ---
        Task<PlatformStats?> GetPlatformStatsAsync();
        Task<bool> SavePlatformStatsAsync(PlatformStats stats);
        Task IncrementPlatformStatAsync(string field, long amount = 1);

        // --- User Activity ---
        Task RecordUserLoginAsync(string tenantId, string upn, string? displayName, string? objectId);
        Task<UserActivityMetrics> GetUserActivityMetricsAsync(string tenantId);
        Task<UserActivityMetrics> GetAllUserActivityMetricsAsync();
        Task<(int uniqueUsers, int loginCount)> GetUserActivityForDateAsync(string? tenantId, DateTime date);

        // --- Metrics Summary (Agent API) ---
        Task<List<object>> GetMetricsSummaryAsync(string? tenantId);

        // --- Rule Stats ---
        Task IncrementRuleStatAsync(string date, string tenantId, string ruleId, string ruleType,
            string ruleTitle, string category, string severity, bool fired, int? confidenceScore);
        Task<bool> SaveRuleStatsEntryAsync(RuleStatsEntry entry);
        Task<List<RuleStatsEntry>> GetRuleStatsAsync(string? tenantId = null, string? startDate = null,
            string? endDate = null, string? ruleType = null, int maxResults = 500);
        Task<int> DeleteRuleStatsOlderThanAsync(DateTime cutoffDate);
    }

    public class UserActivityMetrics
    {
        public int TotalUniqueUsers { get; set; }
        public int DailyLogins { get; set; }
        public int ActiveUsersLast7Days { get; set; }
        public int ActiveUsersLast30Days { get; set; }
    }
}
