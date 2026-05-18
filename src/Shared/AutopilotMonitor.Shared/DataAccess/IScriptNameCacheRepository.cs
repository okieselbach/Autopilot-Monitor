using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models.Graph;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Persists resolved Intune script display names per tenant. Backs the
    /// <c>ScriptNameCache</c> Azure Table.
    /// <para>
    /// Fail-loud — repository methods propagate storage exceptions. The resolver layer
    /// is responsible for catching and degrading to "show ID, no name" so the UI never
    /// breaks because the cache misbehaved.
    /// </para>
    /// </summary>
    public interface IScriptNameCacheRepository
    {
        /// <summary>
        /// Batch lookup for a set of script refs. Returns one entry per requested ref that has
        /// a cache row (positive or NotFound); refs without a row are simply omitted from the
        /// result dictionary.
        /// </summary>
        Task<IReadOnlyDictionary<ScriptRef, ScriptDisplayNameEntry>> GetManyAsync(
            string tenantId, IReadOnlyCollection<ScriptRef> refs, CancellationToken ct = default);

        /// <summary>Upserts a batch of cache rows. Idempotent.</summary>
        Task UpsertManyAsync(string tenantId, IReadOnlyCollection<ScriptDisplayNameEntry> entries, CancellationToken ct = default);

        /// <summary>Returns the per-(tenant,kind) meta row, or null when no full-pull has ever happened.</summary>
        Task<ScriptNameCacheMeta?> TryGetMetaAsync(string tenantId, ScriptKind kind, CancellationToken ct = default);

        /// <summary>Marks a successful full-pull. Idempotent.</summary>
        Task UpsertMetaAsync(ScriptNameCacheMeta meta, CancellationToken ct = default);

        /// <summary>
        /// Enumerates ALL data + meta rows older than the cutoffs. Used by the daily cleanup function.
        /// Positive rows fetched older than <paramref name="positiveCutoff"/>, NotFound rows older
        /// than <paramref name="notFoundCutoff"/>.
        /// </summary>
        IAsyncEnumerable<ScriptDisplayNameEntry> QueryExpiredAsync(
            System.DateTimeOffset positiveCutoff,
            System.DateTimeOffset notFoundCutoff,
            CancellationToken ct = default);

        /// <summary>Removes a single cached row. 404 silently swallowed (idempotent).</summary>
        Task DeleteAsync(string tenantId, ScriptKind kind, string scriptId, CancellationToken ct = default);
    }
}
