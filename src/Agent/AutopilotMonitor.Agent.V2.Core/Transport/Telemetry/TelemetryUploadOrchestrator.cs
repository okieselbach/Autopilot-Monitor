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
            try
            {
                int uploaded = 0;
                int failedBatches = 0;
                string? lastError = null;

                while (!cancellationToken.IsCancellationRequested)
                {
                    var batch = _spool.Peek(_batchSize);
                    if (batch.Count == 0) break;

                    var outcome = await TryUploadWithRetryAsync(batch, cancellationToken).ConfigureAwait(false);

                    if (outcome.Success)
                    {
                        _spool.MarkUploaded(batch[batch.Count - 1].TelemetryItemId);
                        uploaded += batch.Count;
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

                return new DrainResult(uploaded, failedBatches, lastError);
            }
            finally
            {
                _drainGuard.Release();
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
