using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Azure Tables implementation of <see cref="ISignalRepository"/>. Upserts to the
    /// <c>Signals</c> table via entity-group-transactions (chunked to the 100-op limit).
    /// </summary>
    public sealed class TableSignalRepository : ISignalRepository
    {
        /// <summary>Azure Table Storage limit per entity-group-transaction.</summary>
        internal const int TransactionChunkSize = 100;

        private readonly TableStorageService _storage;
        private readonly ILogger<TableSignalRepository> _logger;

        public TableSignalRepository(TableStorageService storage, ILogger<TableSignalRepository> logger)
        {
            _storage = storage;
            _logger = logger;
        }

        public async Task<int> StoreBatchAsync(
            IReadOnlyList<SignalRecord> records,
            CancellationToken cancellationToken = default)
        {
            if (records == null || records.Count == 0) return 0;

            foreach (var r in records)
            {
                SecurityValidator.EnsureValidGuid(r.TenantId, "TenantId");
                SecurityValidator.EnsureValidGuid(r.SessionId, "SessionId");
            }

            var table = _storage.GetTableClient(Constants.TableNames.Signals);
            var committed = 0;

            foreach (var group in records.GroupBy(r => (r.TenantId, r.SessionId)))
            {
                var ordered = group.OrderBy(r => r.SessionSignalOrdinal).ToList();

                for (var offset = 0; offset < ordered.Count; offset += TransactionChunkSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var chunk = ordered.Skip(offset).Take(TransactionChunkSize).ToList();
                    var actions = chunk
                        .Select(r => new TableTransactionAction(
                            TableTransactionActionType.UpsertReplace,
                            ToEntity(r)))
                        .ToList();

                    await table.SubmitTransactionAsync(actions, cancellationToken).ConfigureAwait(false);
                    committed += chunk.Count;
                }

                _logger.LogDebug(
                    "Signals: committed {Count} rows for {Tenant}_{Session}",
                    ordered.Count, group.Key.TenantId, group.Key.SessionId);
            }

            return committed;
        }

        /// <summary>
        /// Projects a <see cref="SignalRecord"/> onto its Azure <see cref="TableEntity"/> shape.
        /// Keys: PK = <c>{TenantId}_{SessionId}</c>, RK = <c>{SessionSignalOrdinal:D19}</c>
        /// (19 digits covers long.MaxValue for lexicographic ordering).
        /// </summary>
        internal static TableEntity ToEntity(SignalRecord r)
        {
            var pk = BuildPartitionKey(r.TenantId, r.SessionId);
            var rk = BuildRowKey(r.SessionSignalOrdinal);

            var entity = new TableEntity(pk, rk)
            {
                ["TenantId"] = r.TenantId,
                ["SessionId"] = r.SessionId,
                ["SessionSignalOrdinal"] = r.SessionSignalOrdinal,
                ["SessionTraceOrdinal"] = r.SessionTraceOrdinal,
                ["Kind"] = r.Kind ?? string.Empty,
                ["KindSchemaVersion"] = r.KindSchemaVersion,
                ["OccurredAtUtc"] = r.OccurredAtUtc,
                ["SourceOrigin"] = r.SourceOrigin ?? string.Empty,
            };

            // Guard the 32 K-char per-property limit — DecisionSignal Evidence+Payload can cross it.
            foreach (var kv in TableStorageChunking.ChunkProperty("PayloadJson", r.PayloadJson ?? string.Empty))
            {
                entity[kv.Key] = kv.Value;
            }

            return entity;
        }

        internal static string BuildPartitionKey(string tenantId, string sessionId)
            => $"{tenantId}_{sessionId}";

        internal static string BuildRowKey(long sessionSignalOrdinal)
            => sessionSignalOrdinal.ToString("D19");
    }
}
