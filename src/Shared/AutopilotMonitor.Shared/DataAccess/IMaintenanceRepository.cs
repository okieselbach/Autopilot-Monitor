using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
        Task<List<SessionSummary>> GetAgentSilentSessionsAsync(string tenantId, DateTime silenceCutoff, DateTime hardCutoff);
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

        // --- Orphan Event Detection ---
        /// <summary>
        /// Returns EventSessionIndex entries whose session no longer exists in the Sessions table
        /// and whose last ingest is older than the grace period.
        /// </summary>
        Task<List<OrphanedEventSession>> GetOrphanedEventSessionsAsync(TimeSpan gracePeriod);
        Task DeleteEventSessionIndexEntryAsync(string tenantId, string sessionId);

        // --- Tenant Offboarding ---
        Task<Dictionary<string, int>> DeleteAllTenantDataAsync(string tenantId);
    }

    public class OrphanedEventSession
    {
        public string TenantId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public DateTime LastIngestAt { get; set; }
        public int EventCount { get; set; }
    }

    public class AuditLogEntry
    {
        public string Id { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string EntityType { get; set; } = string.Empty;
        public string EntityId { get; set; } = string.Empty;
        public string PerformedBy { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public string Details { get; set; } = string.Empty;
    }
}
