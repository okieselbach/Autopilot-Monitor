using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Repository for tracking per-user API usage over time.
    /// Stores per-user, per-day, per-endpoint request counts.
    /// </summary>
    public interface IUserUsageRepository
    {
        /// <summary>
        /// Increments the usage counter for a given user/endpoint/day combination.
        /// Uses optimistic concurrency with retry on conflict.
        /// </summary>
        Task IncrementUsageAsync(string userId, string userPrincipalName, string tenantId, string endpoint);

        /// <summary>
        /// Gets usage records for a specific user within an optional date range.
        /// </summary>
        Task<List<UserUsageRecord>> GetUsageByUserAsync(string userId, string? dateFrom = null, string? dateTo = null);

        /// <summary>
        /// Gets usage records for all users belonging to a tenant within an optional date range.
        /// </summary>
        Task<List<UserUsageRecord>> GetUsageByTenantAsync(string tenantId, string? dateFrom = null, string? dateTo = null);

        /// <summary>
        /// Gets aggregated daily usage summaries, optionally filtered by tenant.
        /// </summary>
        Task<List<UserUsageDailySummary>> GetDailySummaryAsync(string? tenantId = null, string? dateFrom = null, string? dateTo = null);
    }

    /// <summary>
    /// A single usage record: one user, one day, one endpoint.
    /// </summary>
    public class UserUsageRecord
    {
        public string UserId { get; set; } = string.Empty;
        public string UserPrincipalName { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
        public string Date { get; set; } = string.Empty;
        public long RequestCount { get; set; }
        public DateTime LastRequestAt { get; set; }
    }

    /// <summary>
    /// Aggregated daily usage summary across endpoints (and optionally users).
    /// </summary>
    public class UserUsageDailySummary
    {
        public string Date { get; set; } = string.Empty;
        public string? TenantId { get; set; }
        public long TotalRequests { get; set; }
        public int UniqueUsers { get; set; }
        public int UniqueEndpoints { get; set; }
    }
}
