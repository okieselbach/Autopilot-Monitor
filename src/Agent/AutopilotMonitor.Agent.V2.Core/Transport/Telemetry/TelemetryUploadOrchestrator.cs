#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.DecisionCore.Engine;

namespace AutopilotMonitor.Agent.V2.Core.Transport.Telemetry
{
    /// <summary>
    /// Produktiver <see cref="ITelemetryTransport"/>. Plan §2.7a / L.10 / L.17 (Transient-Retry 100/400/1600ms).
    /// <para>
    /// Drain-Semantik (§2.7a): „koordiniert + idempotent + resume-fähig", <b>nicht</b> cross-table-atomar.
    /// Uploads laufen batchweise in <c>TelemetryItemId</c>-Reihenfolge, Backend dedupliziert via
    /// (<c>PartitionKey</c>, <c>RowKey</c>) — Re-Upload nach Retry ist no-op.
    /// </para>
    /// </summary>
    public sealed class TelemetryUploadOrchestrator : ITelemetryTransport
    {
        /// <summary>Plan L.17 — Retry-Backoffs für transiente Fehler.</summary>
        public static readonly IReadOnlyList<TimeSpan> DefaultRetryBackoffs = new[]
        {
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(400),
            TimeSpan.FromMilliseconds(1600),
        };

        private readonly ITelemetrySpool _spool;
        private readonly IBackendTelemetryUploader _uploader;
        private readonly IClock _clock;
        private readonly int _batchSize;
        private readonly IReadOnlyList<TimeSpan> _retryBackoffs;
        private readonly SemaphoreSlim _drainGuard = new SemaphoreSlim(1, 1);
        private bool _disposed;

        // M4.6.ε — backend-to-agent control signals.
        private DateTime? _pausedUntilUtc;

        public TelemetryUploadOrchestrator(
            ITelemetrySpool spool,
            IBackendTelemetryUploader uploader,
            IClock clock,
            int batchSize = 100,
            IReadOnlyList<TimeSpan>? retryBackoffs = null)
        {
            _spool = spool ?? throw new ArgumentNullException(nameof(spool));
            _uploader = uploader ?? throw new ArgumentNullException(nameof(uploader));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));

            if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize), "BatchSize must be positive.");
            _batchSize = batchSize;

            _retryBackoffs = retryBackoffs ?? DefaultRetryBackoffs;
        }

        /// <summary>
        /// Raised once per successful upload — <see cref="UploadResult.OkWithSignals"/> flows
        /// here so Program.cs can synthesize <c>terminate_session</c> <c>ServerAction</c>s from
        /// <see cref="UploadResult.DeviceKillSignal"/> / <see cref="UploadResult.AdminAction"/>
        /// and dispatch <see cref="UploadResult.Actions"/> directly. Plan §4.x M4.6.ε.
        /// <para>
        /// The orchestrator already handles <see cref="UploadResult.DeviceBlocked"/> internally
        /// (pauses the drain loop until <see cref="UploadResult.UnblockAt"/>). Handlers are raised
        /// <b>after</b> <see cref="DrainAllAsync"/> releases its internal drain guard, so it is
        /// safe for a handler to run shutdown code that eventually invokes another drain (e.g.
        /// <c>EnrollmentOrchestrator.Stop</c> → terminal drain). The backend deduplicates
        /// (PartitionKey, RowKey), so any re-entrant drain only uploads genuinely new items.
        /// </para>
        /// </summary>
        public event EventHandler<UploadResult>? ServerResponseReceived;

        /// <summary>
        /// <c>true</c> when the last successful upload returned <see cref="UploadResult.DeviceBlocked"/>
        /// and the paused-until window has not expired. Drain is a no-op in this state.
        /// </summary>
        public bool IsPaused => _pausedUntilUtc.HasValue && _pausedUntilUtc.Value > _clock.UtcNow;

        public long LastUploadedItemId => _spool.LastUploadedItemId;

        public TelemetryItem Enqueue(TelemetryItemDraft draft)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TelemetryUploadOrchestrator));
            return _spool.Enqueue(draft);
        }

        public async Task<DrainResult> DrainAllAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TelemetryUploadOrchestrator));

            await _drainGuard.WaitAsync(cancellationToken).ConfigureAwait(false);

            // Signal-bearing outcomes are collected while the guard is held and raised
            // AFTER release. Raising inline would re-enter drain logic via handlers like
            // onTerminateRequested → orchestrator.Stop → DrainAllAsync, which blocks on the
            // guard and deadlocks the final drain (lost agent_shutting_down). Codex Finding 1.
            List<UploadResult>? pendingSignals = null;
            DrainResult result;

            try
            {
                int uploaded = 0;
                int failedBatches = 0;
                string? lastError = null;

                while (!cancellationToken.IsCancellationRequested)
                {
                    // M4.6.ε — honour a backend-issued DeviceBlocked quarantine. The block is
                    // checked per batch so UnblockAt can auto-resume uploads mid-session.
                    if (IsPaused)
                    {
                        lastError = $"device blocked until {_pausedUntilUtc:O}";
                        break;
                    }

                    var batch = _spool.Peek(_batchSize);
                    if (batch.Count == 0) break;

                    var outcome = await TryUploadWithRetryAsync(batch, cancellationToken).ConfigureAwait(false);

                    if (outcome.Success)
                    {
                        _spool.MarkUploaded(batch[batch.Count - 1].TelemetryItemId);
                        uploaded += batch.Count;

                        // ApplyControlSignals mutates _pausedUntilUtc synchronously so the next
                        // iteration's IsPaused check sees the block. Event dispatch is deferred.
                        ApplyControlSignals(outcome);
                        if (CarriesSignal(outcome))
                        {
                            (pendingSignals ??= new List<UploadResult>()).Add(outcome);
                        }

                        // Stop draining immediately if the backend just quarantined the device —
                        // the next loop iteration would see IsPaused and break anyway, but there
                        // is no point in even Peeking another batch.
                        if (outcome.DeviceBlocked) break;
                    }
                    else
                    {
                        // Stop drain — cursor stays put, re-Peek next drain will return the same
                        // batch (plus anything newly enqueued), backend will dedup on PK/RK.
                        failedBatches++;
                        lastError = outcome.ErrorReason;
                        break;
                    }
                }

                result = new DrainResult(uploaded, failedBatches, lastError);
            }
            finally
            {
                _drainGuard.Release();
            }

            if (pendingSignals != null)
            {
                foreach (var outcome in pendingSignals)
                {
                    RaiseServerResponse(outcome);
                }
            }

            return result;
        }

        private static bool CarriesSignal(UploadResult outcome) =>
            outcome.DeviceBlocked
            || outcome.DeviceKillSignal
            || !string.IsNullOrEmpty(outcome.AdminAction)
            || (outcome.Actions != null && outcome.Actions.Count > 0);

        private void ApplyControlSignals(UploadResult outcome)
        {
            if (outcome.DeviceBlocked)
            {
                // UnblockAt == null → indefinite block, represented as DateTime.MaxValue so
                // IsPaused stays true until Clear() is called (admin un-block on next run).
                _pausedUntilUtc = outcome.UnblockAt ?? DateTime.MaxValue;
            }
        }

        private void RaiseServerResponse(UploadResult outcome)
        {
            if (!CarriesSignal(outcome)) return;

            try { ServerResponseReceived?.Invoke(this, outcome); }
            catch
            {
                // Handler exceptions MUST NOT abort the drain loop — the upload itself succeeded,
                // and the cursor is already forwarded. Swallow + log via the caller's logger chain.
            }
        }

        private async Task<UploadResult> TryUploadWithRetryAsync(
            IReadOnlyList<TelemetryItem> batch,
            CancellationToken cancellationToken)
        {
            UploadResult last = UploadResult.Transient("no-attempt");

            for (int attempt = 0; attempt <= _retryBackoffs.Count; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    last = await _uploader.UploadBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = UploadResult.Transient($"{ex.GetType().Name}: {ex.Message}");
                }

                if (last.Success) return last;
                if (!last.IsTransient) return last;   // permanent — don't retry

                if (attempt < _retryBackoffs.Count)
                {
                    await _clock.Delay(_retryBackoffs[attempt], cancellationToken).ConfigureAwait(false);
                }
            }

            return last;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _drainGuard.Dispose();
        }
    }
}
