using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Repository for the <c>Signals</c> primary table (Plan §M5). Writes projected
    /// <see cref="SignalRecord"/>s derived from agent-emitted DecisionSignals.
    /// <para>
    /// Writes are idempotent via <c>(PartitionKey, RowKey)</c>: a retried batch replays
    /// without duplicating rows. Records within a single batch must share the same
    /// (TenantId, SessionId) — the repository groups/chunks per the Azure Tables 100-op
    /// transaction limit.
    /// </para>
    /// </summary>
    public interface ISignalRepository
    {
        /// <summary>
        /// Upserts a batch of signal records. Returns the number of rows committed.
        /// </summary>
        Task<int> StoreBatchAsync(IReadOnlyList<SignalRecord> records, CancellationToken cancellationToken = default);

        /// <summary>
        /// Reads up to <paramref name="maxResults"/> signal records for a single session,
        /// ordered by <see cref="SignalRecord.SessionSignalOrdinal"/> ascending. Used by the
        /// Inspector read endpoint (<c>GET /api/sessions/{id}/signals</c>, Plan §M5).
        /// </summary>
        Task<List<SignalRecord>> QueryBySessionAsync(
            string tenantId, string sessionId, int maxResults = 1000, CancellationToken cancellationToken = default);
    }
}
