using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Functions.Services;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Table Storage implementation of IMaintenanceRepository.
    /// Delegates to existing TableStorageService for backwards compatibility.
    /// </summary>
    public class TableMaintenanceRepository : IMaintenanceRepository
    {
        private readonly TableStorageService _storage;

        public TableMaintenanceRepository(TableStorageService storage)
        {
            _storage = storage;
        }

        public Task<bool> LogAuditEntryAsync(string tenantId, string action, string entityType,
            string entityId, string performedBy, Dictionary<string, string>? details = null)
            => _storage.LogAuditEntryAsync(tenantId, action, entityType, entityId, performedBy, details);

        public Task<List<AuditLogEntry>> GetAuditLogsAsync(string tenantId, int maxResults = 100)
            => _storage.GetAuditLogsAsync(tenantId, maxResults);

        public Task<List<AuditLogEntry>> GetAllAuditLogsAsync(int maxResults = 100)
            => _storage.GetAllAuditLogsAsync(maxResults);

        public Task<List<SessionSummary>> GetSessionsOlderThanAsync(string tenantId, DateTime cutoffDate)
            => _storage.GetSessionsOlderThanAsync(tenantId, cutoffDate);

        public Task<List<SessionSummary>> GetSessionsByDateRangeAsync(DateTime startDate, DateTime endDate, string? tenantId = null)
            => _storage.GetSessionsByDateRangeAsync(startDate, endDate, tenantId);

        public Task<List<SessionSummary>> GetStalledSessionsAsync(string tenantId, DateTime cutoffTime)
            => _storage.GetStalledSessionsAsync(tenantId, cutoffTime);

        public Task<List<SessionSummary>> GetExcessiveDataSendersAsync(string tenantId, DateTime windowCutoff, int maxSessionWindowHours)
            => _storage.GetExcessiveDataSendersAsync(tenantId, windowCutoff, maxSessionWindowHours);

        public Task<List<string>> GetAllTenantIdsAsync()
            => _storage.GetAllTenantIdsAsync();

        public Task<int> DeleteSessionEventsAsync(string tenantId, string sessionId)
            => _storage.DeleteSessionEventsAsync(tenantId, sessionId);

        public Task<int> DeleteSessionRuleResultsAsync(string tenantId, string sessionId)
            => _storage.DeleteSessionRuleResultsAsync(tenantId, sessionId);

        public Task<int> DeleteSessionAppInstallSummariesAsync(string tenantId, string sessionId)
            => _storage.DeleteSessionAppInstallSummariesAsync(tenantId, sessionId);

        public Task<int> BackfillSessionIndexAsync()
            => _storage.BackfillSessionIndexAsync();

        public Task<int> CleanupGhostSessionIndexEntriesAsync()
            => _storage.CleanupGhostSessionIndexEntriesAsync();

        public Task<bool> IsSessionIndexEmptyAsync()
            => _storage.IsSessionIndexEmptyAsync();
    }
}
