using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Repository for audit logs and data retention/maintenance operations.
    /// Covers: AuditLogs table + maintenance queries across Sessions/Events/RuleResults.
    /// </summary>
    public interface IMaintenanceRepository
    {
        // --- Audit Logs ---
        Task<bool> LogAuditEntryAsync(string tenantId, string action, string entityType,
            string entityId, string performedBy, Dictionary<string, string>? details = null);
        Task<List<AuditLogEntry>> GetAuditLogsAsync(string tenantId, int maxResults = 100);
        Task<List<AuditLogEntry>> GetAllAuditLogsAsync(int maxResults = 100);

        // --- Data Retention Queries ---
        Task<List<SessionSummary>> GetSessionsOlderThanAsync(string tenantId, DateTime cutoffDate);
        Task<List<SessionSummary>> GetSessionsByDateRangeAsync(DateTime startDate, DateTime endDate, string? tenantId = null);
        Task<List<SessionSummary>> GetStalledSessionsAsync(string tenantId, DateTime cutoffTime);
        Task<List<SessionSummary>> GetExcessiveDataSendersAsync(string tenantId, DateTime windowCutoff, int maxSessionWindowHours);

        // --- Tenant Discovery ---
        Task<List<string>> GetAllTenantIdsAsync();

        // --- Cleanup ---
        Task<int> DeleteSessionEventsAsync(string tenantId, string sessionId);
        Task<int> DeleteSessionRuleResultsAsync(string tenantId, string sessionId);
        Task<int> DeleteSessionAppInstallSummariesAsync(string tenantId, string sessionId);

        // --- Index Maintenance ---
        Task<int> BackfillSessionIndexAsync();
        Task<int> CleanupGhostSessionIndexEntriesAsync();
        Task<bool> IsSessionIndexEmptyAsync();

        // --- Tenant Offboarding ---
        Task<Dictionary<string, int>> DeleteAllTenantDataAsync(string tenantId);
    }
}
