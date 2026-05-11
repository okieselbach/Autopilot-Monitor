using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Persistence for per-tenant SLA breach state. One row per tenant.
    /// Single-row reads/writes scope queries to a single partition so cross-tenant
    /// access is impossible at the storage layer.
    /// </summary>
    public interface ISlaTenantStatusRepository
    {
        /// <summary>Returns the status row for a tenant, or null if no row exists.</summary>
        Task<SlaTenantStatus?> GetAsync(string tenantId);

        /// <summary>
        /// Returns the status row for a tenant together with an opaque ETag.
        /// Pass the returned token back to <see cref="TryUpsertAsync"/> for atomic
        /// (compare-and-swap) writes. ETag is <c>null</c> when no row exists yet —
        /// the caller should call <see cref="TryUpsertAsync"/> with the same null
        /// token to ask the repository to create the row.
        /// </summary>
        Task<(SlaTenantStatus? Status, string? ETag)> GetWithETagAsync(string tenantId);

        /// <summary>Upserts the status row, no concurrency check (last-writer-wins).</summary>
        Task<bool> UpsertAsync(SlaTenantStatus status);

        /// <summary>
        /// Conditionally upserts the status row. When <paramref name="ifMatchETag"/> is
        /// <c>null</c> the call attempts to insert a new row; if a row already exists
        /// the call returns <c>false</c>. When the ETag does not match the current row,
        /// returns <c>false</c> so callers can refetch and retry.
        /// </summary>
        Task<bool> TryUpsertAsync(SlaTenantStatus status, string? ifMatchETag);

        /// <summary>
        /// Returns all tenant status rows where at least one breach type is currently active.
        /// Used by the GA cross-tenant overview.
        /// </summary>
        Task<List<SlaTenantStatus>> ListAllActiveAsync();

        /// <summary>
        /// Returns every status row in the table, active or not. Used by the timer
        /// path to detect tenants whose toggles were disabled while a breach was active
        /// so the row can be silently cleared (no zombie GA entries).
        /// </summary>
        Task<List<SlaTenantStatus>> ListAllAsync();
    }
}
