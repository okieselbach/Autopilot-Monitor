using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Repository for tracking API key usage over time.
    /// Stores per-key, per-day, per-endpoint request counts.
    /// </summary>
    public interface IApiUsageRepository
    {
        /// <summary>
        /// Increments the usage counter for a given key/endpoint/day combination.
        /// Uses optimistic concurrency with retry on conflict.
        /// </summary>
        Task IncrementUsageAsync(string keyId, string tenantId, string scope, string endpoint);

        /// <summary>
        /// Gets usage records for a specific API key within an optional date range.
        /// </summary>
        Task<List<ApiUsageRecord>> GetUsageByKeyAsync(string keyId, string? dateFrom = null, string? dateTo = null);

        /// <summary>
        /// Gets usage records for all keys belonging to a tenant within an optional date range.
        /// </summary>
        Task<List<ApiUsageRecord>> GetUsageByTenantAsync(string tenantId, string? dateFrom = null, string? dateTo = null);

        /// <summary>
        /// Gets aggregated daily usage summaries, optionally filtered by tenant.
        /// </summary>
        Task<List<ApiUsageDailySummary>> GetDailySummaryAsync(string? tenantId = null, string? dateFrom = null, string? dateTo = null);
    }

    /// <summary>
    /// A single usage record: one API key, one day, one endpoint.
    /// </summary>
    public class ApiUsageRecord
    {
        public string KeyId { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public string Scope { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
        public string Date { get; set; } = string.Empty;
        public long RequestCount { get; set; }
        public DateTime LastRequestAt { get; set; }
    }

    /// <summary>
    /// Aggregated daily usage summary across endpoints (and optionally keys).
    /// </summary>
    public class ApiUsageDailySummary
    {
        public string Date { get; set; } = string.Empty;
        public string? TenantId { get; set; }
        public long TotalRequests { get; set; }
        public int UniqueKeys { get; set; }
        public int UniqueEndpoints { get; set; }
    }
}
