using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Repository for pre-auth distress reports (DistressReports table).
    /// All data is unverified — the distress channel has no authentication.
    /// </summary>
    public interface IDistressReportRepository
    {
        Task SaveDistressReportAsync(string tenantId, DistressReportEntry entry);
        Task<List<DistressReportEntry>> GetDistressReportsAsync(string tenantId, int maxResults = 100);
        Task<List<DistressReportEntry>> GetAllDistressReportsAsync(int maxResults = 500);
        Task<int> DeleteDistressReportsOlderThanAsync(string tenantId, DateTime cutoff);
    }

    public class DistressReportEntry
    {
        public string TenantId { get; set; } = string.Empty;
        public string ErrorType { get; set; } = string.Empty;
        public string? Manufacturer { get; set; }
        public string? Model { get; set; }
        public string? SerialNumber { get; set; }
        public string? AgentVersion { get; set; }
        public int? HttpStatusCode { get; set; }
        public string? Message { get; set; }
        public DateTime AgentTimestamp { get; set; }
        public DateTime IngestedAt { get; set; }
        public string? SourceIp { get; set; }
    }
}
