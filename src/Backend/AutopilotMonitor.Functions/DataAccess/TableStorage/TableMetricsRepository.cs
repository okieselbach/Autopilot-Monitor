using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Functions.Services;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Table Storage implementation of IMetricsRepository.
    /// Delegates to existing TableStorageService for backwards compatibility.
    /// </summary>
    public class TableMetricsRepository : IMetricsRepository
    {
        private readonly TableStorageService _storage;
        private readonly IDataEventPublisher _publisher;

        public TableMetricsRepository(TableStorageService storage, IDataEventPublisher publisher)
        {
            _storage = storage;
            _publisher = publisher;
        }

        public Task<bool> SaveUsageMetricsSnapshotAsync(UsageMetricsSnapshot metrics)
            => _storage.SaveUsageMetricsSnapshotAsync(metrics);

        public Task<List<UsageMetricsSnapshot>> GetUsageMetricsSnapshotAsync(
            string? tenantId = null, string? startDate = null, string? endDate = null, int maxResults = 100)
            => _storage.GetUsageMetricsSnapshotAsync(tenantId, startDate, endDate, maxResults);

        public Task<bool> HasUsageMetricsSnapshotAsync(string date)
            => _storage.HasUsageMetricsSnapshotAsync(date);

        public Task<bool> StoreAppInstallSummaryAsync(AppInstallSummary summary)
            => _storage.StoreAppInstallSummaryAsync(summary);

        public Task<List<AppInstallSummary>> GetAppInstallSummariesByTenantAsync(string tenantId)
            => _storage.GetAppInstallSummariesByTenantAsync(tenantId);

        public Task<List<AppInstallSummary>> GetAllAppInstallSummariesAsync()
            => _storage.GetAllAppInstallSummariesAsync();

        public Task<PlatformStats?> GetPlatformStatsAsync()
            => _storage.GetPlatformStatsAsync();

        public Task<bool> SavePlatformStatsAsync(PlatformStats stats)
            => _storage.SavePlatformStatsAsync(stats);

        public Task IncrementPlatformStatAsync(string field, long amount = 1)
            => _storage.IncrementPlatformStatAsync(field, amount);

        public Task RecordUserLoginAsync(string tenantId, string upn, string? displayName, string? objectId)
            => _storage.RecordUserLoginAsync(tenantId, upn, displayName, objectId);

        public Task<UserActivityMetrics> GetUserActivityMetricsAsync(string tenantId)
            => _storage.GetUserActivityMetricsAsync(tenantId);

        public Task<UserActivityMetrics> GetAllUserActivityMetricsAsync()
            => _storage.GetAllUserActivityMetricsAsync();

        public Task<(int uniqueUsers, int loginCount)> GetUserActivityForDateAsync(string? tenantId, DateTime date)
            => _storage.GetUserActivityForDateAsync(tenantId, date);

        public Task<List<object>> GetMetricsSummaryAsync(string? tenantId)
            => _storage.GetMetricsSummaryAsync(tenantId);

        public Task IncrementRuleStatAsync(string date, string tenantId, string ruleId, string ruleType,
            string ruleTitle, string category, string severity, bool fired, int? confidenceScore)
            => _storage.IncrementRuleStatAsync(date, tenantId, ruleId, ruleType, ruleTitle, category, severity, fired, confidenceScore);

        public Task<bool> SaveRuleStatsEntryAsync(RuleStatsEntry entry)
            => _storage.SaveRuleStatsEntryAsync(entry);

        public Task<List<RuleStatsEntry>> GetRuleStatsAsync(string? tenantId = null, string? startDate = null,
            string? endDate = null, string? ruleType = null, int maxResults = 500)
            => _storage.GetRuleStatsAsync(tenantId, startDate, endDate, ruleType, maxResults);

        public Task<int> DeleteRuleStatsOlderThanAsync(DateTime cutoffDate)
            => _storage.DeleteRuleStatsOlderThanAsync(cutoffDate);
    }
}
