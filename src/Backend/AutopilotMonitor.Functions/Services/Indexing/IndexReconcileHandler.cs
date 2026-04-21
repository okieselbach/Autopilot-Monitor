using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Indexing
{
    /// <summary>
    /// Fan-out logic for <see cref="IndexReconcileEnvelope"/>s consumed from the
    /// <c>telemetry-index-reconcile</c> queue (Plan §2.8, §M5.d.3). Projects a single
    /// envelope into the 0–3 applicable index-table rows via <see cref="IIndexTableRepository"/>.
    /// <para>
    /// <b>Routing:</b>
    /// <list type="bullet">
    ///   <item><c>Signal</c> → <c>SignalsByKind</c> (always, 1 row).</item>
    ///   <item><c>DecisionTransition</c>, <c>IsTerminal=true</c> → <c>SessionsByTerminal</c>.</item>
    ///   <item><c>DecisionTransition</c>, <c>Taken=true</c> → <c>SessionsByStage</c>
    ///     (session entered a new stage; older rows for the same SessionId in other stage
    ///     PKs remain until the 2h reconcile timer prunes or the next stage entry supersedes
    ///     them in query-time ordering).</item>
    ///   <item><c>DecisionTransition</c>, <c>Taken=false</c> + non-null <c>DeadEndReason</c>
    ///     → <c>DeadEndsByReason</c>.</item>
    ///   <item><c>DecisionTransition</c> with non-null <c>ClassifierVerdictId</c>
    ///     → <c>ClassifierVerdictsByIdLevel</c>.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Failure semantics:</b> malformed envelopes (unknown <see cref="IndexReconcileEnvelope.SourceKind"/>,
    /// missing required discriminators) are logged + dropped — retrying them against a poison
    /// queue won't help. Transient Table-Storage errors rethrow, so the QueueTrigger platform
    /// retry + poison-queue machinery kicks in.
    /// </para>
    /// <para>
    /// <b>Plain class (no <c>I</c>-interface):</b> repo layer is already DAL-abstracted via
    /// <see cref="IIndexTableRepository"/>; wrapping the handler too would be Cosmos-swap bloat.
    /// Testability comes from constructor-injecting a fake repo.
    /// </para>
    /// </summary>
    public class IndexReconcileHandler
    {
        private readonly IIndexTableRepository _indexRepo;
        private readonly ILogger<IndexReconcileHandler> _logger;

        public IndexReconcileHandler(
            IIndexTableRepository indexRepo,
            ILogger<IndexReconcileHandler> logger)
        {
            _indexRepo = indexRepo;
            _logger = logger;
        }

        public Task HandleAsync(IndexReconcileEnvelope envelope, CancellationToken cancellationToken = default)
        {
            if (envelope is null)
            {
                _logger.LogWarning("IndexReconcile: null envelope — dropping");
                return Task.CompletedTask;
            }

            return envelope.SourceKind switch
            {
                "Signal"             => HandleSignalAsync(envelope, cancellationToken),
                "DecisionTransition" => HandleDecisionTransitionAsync(envelope, cancellationToken),
                _                    => LogAndDrop(envelope),
            };
        }

        private Task LogAndDrop(IndexReconcileEnvelope envelope)
        {
            _logger.LogWarning(
                "IndexReconcile: unknown SourceKind '{Kind}' (envelopeVersion={Version}) — dropping",
                envelope.SourceKind, envelope.EnvelopeVersion);
            return Task.CompletedTask;
        }

        // ============================================================ Signal path

        private async Task HandleSignalAsync(IndexReconcileEnvelope e, CancellationToken ct)
        {
            if (e.SessionSignalOrdinal is not long ordinal || string.IsNullOrEmpty(e.SignalKind))
            {
                _logger.LogWarning(
                    "IndexReconcile Signal envelope missing SessionSignalOrdinal or SignalKind (tenant={Tenant} session={Session}) — dropping",
                    e.TenantId, e.SessionId);
                return;
            }

            await _indexRepo.StoreSignalsByKindAsync(
                new[]
                {
                    new SignalsByKindRecord
                    {
                        TenantId             = e.TenantId,
                        SessionId            = e.SessionId,
                        SignalKind           = e.SignalKind,
                        SessionSignalOrdinal = ordinal,
                        OccurredAtUtc        = e.OccurredAtUtc,
                        SourceOrigin         = e.SourceOrigin ?? string.Empty,
                    }
                },
                ct).ConfigureAwait(false);
        }

        // ============================================================ DecisionTransition path

        private async Task HandleDecisionTransitionAsync(IndexReconcileEnvelope e, CancellationToken ct)
        {
            if (e.StepIndex is not int stepIndex)
            {
                _logger.LogWarning(
                    "IndexReconcile DecisionTransition envelope missing StepIndex (tenant={Tenant} session={Session}) — dropping",
                    e.TenantId, e.SessionId);
                return;
            }

            var writes = 0;

            // SessionsByTerminal — only if a terminal stage was reached.
            if (e.IsTerminal == true && !string.IsNullOrEmpty(e.ToStage))
            {
                await _indexRepo.StoreSessionsByTerminalAsync(
                    new[]
                    {
                        new SessionsByTerminalRecord
                        {
                            TenantId      = e.TenantId,
                            SessionId     = e.SessionId,
                            TerminalStage = e.ToStage!,
                            OccurredAtUtc = e.OccurredAtUtc,
                            StepIndex     = stepIndex,
                        }
                    },
                    ct).ConfigureAwait(false);
                writes++;
            }

            // SessionsByStage — every Taken transition advances the "current stage" row.
            if (e.Taken == true && !string.IsNullOrEmpty(e.ToStage))
            {
                await _indexRepo.StoreSessionsByStageAsync(
                    new[]
                    {
                        new SessionsByStageRecord
                        {
                            TenantId       = e.TenantId,
                            SessionId      = e.SessionId,
                            Stage          = e.ToStage!,
                            LastUpdatedUtc = e.OccurredAtUtc,
                            StepIndex      = stepIndex,
                        }
                    },
                    ct).ConfigureAwait(false);
                writes++;
            }

            // DeadEndsByReason — blocked transitions with an explained reason.
            if (e.Taken == false && !string.IsNullOrEmpty(e.DeadEndReason))
            {
                await _indexRepo.StoreDeadEndsByReasonAsync(
                    new[]
                    {
                        new DeadEndsByReasonRecord
                        {
                            TenantId         = e.TenantId,
                            SessionId        = e.SessionId,
                            DeadEndReason    = e.DeadEndReason!,
                            StepIndex        = stepIndex,
                            FromStage        = e.FromStage ?? string.Empty,
                            AttemptedToStage = e.ToStage ?? string.Empty,
                            OccurredAtUtc    = e.OccurredAtUtc,
                        }
                    },
                    ct).ConfigureAwait(false);
                writes++;
            }

            // ClassifierVerdictsByIdLevel — any transition step that recorded a classifier verdict.
            if (!string.IsNullOrEmpty(e.ClassifierVerdictId))
            {
                await _indexRepo.StoreClassifierVerdictsByIdLevelAsync(
                    new[]
                    {
                        new ClassifierVerdictsByIdLevelRecord
                        {
                            TenantId        = e.TenantId,
                            SessionId       = e.SessionId,
                            ClassifierId    = e.ClassifierVerdictId!,
                            HypothesisLevel = e.ClassifierHypothesisLevel ?? string.Empty,
                            StepIndex       = stepIndex,
                            OccurredAtUtc   = e.OccurredAtUtc,
                        }
                    },
                    ct).ConfigureAwait(false);
                writes++;
            }

            _logger.LogDebug(
                "IndexReconcile DecisionTransition handled (tenant={Tenant} session={Session} step={Step}): {Writes} index row(s)",
                e.TenantId, e.SessionId, stepIndex, writes);
        }
    }
}
