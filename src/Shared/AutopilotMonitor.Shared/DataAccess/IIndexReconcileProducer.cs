using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Enqueues <see cref="IndexReconcileEnvelope"/>s onto the
    /// <c>telemetry-index-reconcile</c> queue (Plan §2.8, §M5.d). Implementations MUST
    /// no-op when the <c>AdminConfiguration.EnableIndexDualWrite</c> flag is false —
    /// this is the rollout gate that keeps pre-M5.d behaviour bit-exact by default.
    /// <para>
    /// Exceptions from the underlying queue should NOT propagate back to the ingest
    /// path. The primary-row write is the source of truth; index rows are eventually
    /// consistent and the 2h reconcile timer (M5.d.4) is the safety net for failures.
    /// </para>
    /// </summary>
    public interface IIndexReconcileProducer
    {
        /// <summary>
        /// Enqueues one queue message per envelope. Returns the number of messages actually
        /// sent (0 if the feature flag is off or the batch is empty).
        /// </summary>
        Task<int> EnqueueBatchAsync(
            IReadOnlyList<IndexReconcileEnvelope> envelopes,
            CancellationToken cancellationToken = default);
    }
}
