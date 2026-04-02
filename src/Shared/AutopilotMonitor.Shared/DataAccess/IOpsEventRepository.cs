using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Repository for operational events (OpsEvents table).
    /// Stores vital infrastructure events visible to Global Admins in the Ops dashboard.
    /// </summary>
    public interface IOpsEventRepository
    {
        Task SaveOpsEventAsync(OpsEventEntry entry);
        Task<List<OpsEventEntry>> GetOpsEventsAsync(int maxResults = 200);
        Task<List<OpsEventEntry>> GetOpsEventsByCategoryAsync(string category, int maxResults = 100);
        Task<int> DeleteOpsEventsOlderThanAsync(DateTime cutoff);
    }

    /// <summary>
    /// Categories for operational events.
    /// </summary>
    public static class OpsEventCategory
    {
        public const string Consent = "Consent";
        public const string Maintenance = "Maintenance";
        public const string Security = "Security";
        public const string Tenant = "Tenant";
        public const string Agent = "Agent";
    }

    /// <summary>
    /// Severity levels for operational events.
    /// </summary>
    public static class OpsEventSeverity
    {
        public const string Info = "Info";
        public const string Warning = "Warning";
        public const string Error = "Error";
        public const string Critical = "Critical";
    }

    public class OpsEventEntry
    {
        public string Id { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string EventType { get; set; } = string.Empty;
        public string Severity { get; set; } = OpsEventSeverity.Info;
        public string? TenantId { get; set; }
        public string? UserId { get; set; }
        public string Message { get; set; } = string.Empty;
        public string? Details { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
