using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Repository for the <c>DecisionTransitions</c> primary table (Plan §M5). Writes projected
    /// <see cref="DecisionTransitionRecord"/>s derived from agent Journal entries.
    /// <para>
    /// Writes are idempotent via <c>(PartitionKey, RowKey)</c>: a retried batch replays without
    /// duplicating rows. Records within a single batch must share the same (TenantId, SessionId).
    /// </para>
    /// </summary>
    public interface IDecisionTransitionRepository
    {
        /// <summary>
        /// Upserts a batch of transition records. Returns the number of rows committed.
        /// </summary>
        Task<int> StoreBatchAsync(IReadOnlyList<DecisionTransitionRecord> records, CancellationToken cancellationToken = default);

        /// <summary>
        /// Reads up to <paramref name="maxResults"/> transition records for a single session,
        /// ordered by <see cref="DecisionTransitionRecord.StepIndex"/> ascending. Used by the
        /// Inspector decision-graph endpoint (<c>GET /api/sessions/{id}/decision-graph</c>, Plan §M5).
        /// </summary>
        Task<List<DecisionTransitionRecord>> QueryBySessionAsync(
            string tenantId, string sessionId, int maxResults = 1000, CancellationToken cancellationToken = default);

        /// <summary>
        /// Cross-partition scan of all decision transitions whose Azure Tables server-side
        /// <c>Timestamp</c> is at or after <paramref name="cutoffUtc"/>. Used by the
        /// <c>IndexReconcileTimer</c> (Plan §M5.d.4) to re-enqueue the last 4h of primary
        /// rows as a safety-net against queue failures.
        /// <para>
        /// <b>Perf note:</b> Azure Tables has no PK-bound filter here, so this is a scan;
        /// <paramref name="maxResults"/> bounds memory. If the cap is reached the caller
        /// should log + narrow the window rather than trust completeness.
        /// </para>
        /// </summary>
        Task<List<DecisionTransitionRecord>> QueryByTimestampAtOrAfterAsync(
            DateTime cutoffUtc, int maxResults = 50_000, CancellationToken cancellationToken = default);
    }
}
