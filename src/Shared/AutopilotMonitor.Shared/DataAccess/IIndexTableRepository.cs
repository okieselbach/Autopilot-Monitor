using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Write-only repository for the 5 V2 Decision Engine index tables
    /// (Plan §2.8 query matrix, §M5.d). Drives async fan-out from the primary
    /// <c>Signals</c> / <c>DecisionTransitions</c> rows after they've committed, via the
    /// <c>telemetry-index-reconcile</c> queue and the 2h safety-net timer.
    /// <para>
    /// All writes are idempotent via <c>(PartitionKey, RowKey)</c>: a retried reconcile
    /// replays without duplicating rows. <see cref="Azure.Data.Tables.TableTransactionAction"/>
    /// batches require a shared PartitionKey, so the implementation groups + chunks input
    /// per the 100-op transaction limit. Callers should partition by target table before
    /// invoking the corresponding <c>Store…Async</c> method.
    /// </para>
    /// </summary>
    public interface IIndexTableRepository
    {
        Task<int> StoreSessionsByTerminalAsync(
            IReadOnlyList<SessionsByTerminalRecord> records,
            CancellationToken cancellationToken = default);

        Task<int> StoreSessionsByStageAsync(
            IReadOnlyList<SessionsByStageRecord> records,
            CancellationToken cancellationToken = default);

        Task<int> StoreDeadEndsByReasonAsync(
            IReadOnlyList<DeadEndsByReasonRecord> records,
            CancellationToken cancellationToken = default);

        Task<int> StoreClassifierVerdictsByIdLevelAsync(
            IReadOnlyList<ClassifierVerdictsByIdLevelRecord> records,
            CancellationToken cancellationToken = default);

        Task<int> StoreSignalsByKindAsync(
            IReadOnlyList<SignalsByKindRecord> records,
            CancellationToken cancellationToken = default);
    }
}
