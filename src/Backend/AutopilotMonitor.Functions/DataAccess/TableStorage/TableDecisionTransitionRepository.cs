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
    /// Azure Tables implementation of <see cref="IDecisionTransitionRepository"/>. Upserts to
    /// the <c>DecisionTransitions</c> table via entity-group-transactions (chunked to the 100-op
    /// limit).
    /// </summary>
    public sealed class TableDecisionTransitionRepository : IDecisionTransitionRepository
    {
        /// <summary>Azure Table Storage limit per entity-group-transaction.</summary>
        internal const int TransactionChunkSize = 100;

        private readonly TableStorageService _storage;
        private readonly ILogger<TableDecisionTransitionRepository> _logger;

        public TableDecisionTransitionRepository(
            TableStorageService storage,
            ILogger<TableDecisionTransitionRepository> logger)
        {
            _storage = storage;
            _logger = logger;
        }

        public async Task<int> StoreBatchAsync(
            IReadOnlyList<DecisionTransitionRecord> records,
            CancellationToken cancellationToken = default)
        {
            if (records == null || records.Count == 0) return 0;

            foreach (var r in records)
            {
                SecurityValidator.EnsureValidGuid(r.TenantId, "TenantId");
                SecurityValidator.EnsureValidGuid(r.SessionId, "SessionId");
            }

            var table = _storage.GetTableClient(Constants.TableNames.DecisionTransitions);
            var committed = 0;

            foreach (var group in records.GroupBy(r => (r.TenantId, r.SessionId)))
            {
                var ordered = group.OrderBy(r => r.StepIndex).ToList();

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
                    "DecisionTransitions: committed {Count} rows for {Tenant}_{Session}",
                    ordered.Count, group.Key.TenantId, group.Key.SessionId);
            }

            return committed;
        }

        /// <summary>
        /// Projects a <see cref="DecisionTransitionRecord"/> onto its Azure <see cref="TableEntity"/>
        /// shape. Keys: PK = <c>{TenantId}_{SessionId}</c>, RK = <c>{StepIndex:D10}</c>.
        /// </summary>
        internal static TableEntity ToEntity(DecisionTransitionRecord r)
        {
            var pk = BuildPartitionKey(r.TenantId, r.SessionId);
            var rk = BuildRowKey(r.StepIndex);

            var entity = new TableEntity(pk, rk)
            {
                ["TenantId"] = r.TenantId,
                ["SessionId"] = r.SessionId,
                ["StepIndex"] = r.StepIndex,
                ["SessionTraceOrdinal"] = r.SessionTraceOrdinal,
                ["SignalOrdinalRef"] = r.SignalOrdinalRef,
                ["OccurredAtUtc"] = r.OccurredAtUtc,
                ["Trigger"] = r.Trigger ?? string.Empty,
                ["FromStage"] = r.FromStage ?? string.Empty,
                ["ToStage"] = r.ToStage ?? string.Empty,
                ["Taken"] = r.Taken,
                ["DeadEndReason"] = r.DeadEndReason,
                ["ReducerVersion"] = r.ReducerVersion ?? string.Empty,
                ["IsTerminal"] = r.IsTerminal,
                ["ClassifierVerdictId"] = r.ClassifierVerdictId,
                ["ClassifierHypothesisLevel"] = r.ClassifierHypothesisLevel,
            };

            foreach (var kv in TableStorageChunking.ChunkProperty("PayloadJson", r.PayloadJson ?? string.Empty))
            {
                entity[kv.Key] = kv.Value;
            }

            return entity;
        }

        internal static string BuildPartitionKey(string tenantId, string sessionId)
            => $"{tenantId}_{sessionId}";

        internal static string BuildRowKey(int stepIndex)
            => stepIndex.ToString("D10");
    }
}
