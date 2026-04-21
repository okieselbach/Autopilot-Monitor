using System;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Indexing;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Admin
{
    /// <summary>
    /// 2h safety-net timer (Plan §2.8, §M5.d.4) that recovers missed index-table writes.
    /// Scans the last 4h of primary <c>Signals</c> + <c>DecisionTransitions</c> rows and
    /// re-enqueues envelopes onto the <c>telemetry-index-reconcile</c> queue via the
    /// same producer the hot ingest path uses. The queue-triggered handler (M5.d.3)
    /// then upserts index rows idempotently.
    /// <para>
    /// <b>Why 2h / 4h:</b> window is deliberately larger than cadence so every row
    /// gets re-checked at least twice before falling out of scan range. Queue failures
    /// or pod restarts during ingest are recoverable within the overlap.
    /// </para>
    /// <para>
    /// <b>Feature gate:</b> <see cref="Shared.Models.AdminConfiguration.EnableIndexDualWrite"/>.
    /// When false, the scan is short-circuited — no queries, no enqueues.
    /// </para>
    /// </summary>
    public sealed class IndexReconcileTimer
    {
        // Cron "0 0 */2 * * *" = top of every second hour (00:00, 02:00, 04:00 UTC, …)
        // — matches the MaintenanceFunction cadence so ops only has two timer heartbeats
        // to babysit.
        private const string Cron = "0 0 */2 * * *";

        /// <summary>Safety-net window: scan primaries committed in the last N hours.</summary>
        internal static readonly TimeSpan ReconcileWindow = TimeSpan.FromHours(4);

        private readonly AdminConfigurationService _adminConfig;
        private readonly ISignalRepository _signalRepo;
        private readonly IDecisionTransitionRepository _transitionRepo;
        private readonly IIndexReconcileProducer _producer;
        private readonly ILogger<IndexReconcileTimer> _logger;

        public IndexReconcileTimer(
            AdminConfigurationService adminConfig,
            ISignalRepository signalRepo,
            IDecisionTransitionRepository transitionRepo,
            IIndexReconcileProducer producer,
            ILogger<IndexReconcileTimer> logger)
        {
            _adminConfig = adminConfig;
            _signalRepo = signalRepo;
            _transitionRepo = transitionRepo;
            _producer = producer;
            _logger = logger;
        }

        [Function("IndexReconcileTimer")]
        public Task Run([TimerTrigger(Cron)] object timer, CancellationToken cancellationToken)
        {
            var cutoff = DateTime.UtcNow - ReconcileWindow;
            return RunReconcileAsync(cutoff, cancellationToken);
        }

        /// <summary>
        /// Testable core. Separated from the TimerTrigger entry so unit tests can drive it
        /// with a deterministic cutoff without an Azure Functions host.
        /// </summary>
        internal async Task RunReconcileAsync(DateTime cutoffUtc, CancellationToken cancellationToken)
        {
            var config = await _adminConfig.GetConfigurationAsync().ConfigureAwait(false);
            if (!config.EnableIndexDualWrite)
            {
                _logger.LogInformation(
                    "IndexReconcileTimer: EnableIndexDualWrite=false — skipping scan (cutoff {Cutoff:o})",
                    cutoffUtc);
                return;
            }

            var signals = await _signalRepo
                .QueryByTimestampAtOrAfterAsync(cutoffUtc, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var transitions = await _transitionRepo
                .QueryByTimestampAtOrAfterAsync(cutoffUtc, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var envelopes = IndexReconcileEnvelopeFactory.BuildBatch(signals, transitions);

            _logger.LogInformation(
                "IndexReconcileTimer: re-enqueueing {Count} envelope(s) ({S} signals + {T} transitions) since {Cutoff:o}",
                envelopes.Count, signals.Count, transitions.Count, cutoffUtc);

            if (envelopes.Count == 0) return;

            // The producer is already flag-gated and swallows queue-side failures. We trust
            // its contract here — timer never throws back into the host.
            await _producer.EnqueueBatchAsync(envelopes, cancellationToken).ConfigureAwait(false);
        }
    }
}
