using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Transport.Telemetry;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Telemetry.Events
{
    /// <summary>
    /// In-memory ITelemetryTransport that captures every Enqueue call. Builds a fully-populated
    /// <see cref="TelemetryItem"/> (assigned TelemetryItemId, SessionTraceOrdinal when session-scoped),
    /// so callers can verify field propagation without disk-backed plumbing.
    /// </summary>
    internal sealed class FakeTelemetryTransport : ITelemetryTransport
    {
        private readonly object _lock = new object();
        private readonly List<TelemetryItem> _enqueued = new List<TelemetryItem>();
        private long _nextItemId = 0;

        public IReadOnlyList<TelemetryItem> Enqueued
        {
            get { lock (_lock) return _enqueued.ToArray(); }
        }

        public int EnqueueCount
        {
            get { lock (_lock) return _enqueued.Count; }
        }

        public long LastUploadedItemId => -1;

        public TelemetryItem Enqueue(TelemetryItemDraft draft)
        {
            if (draft == null) throw new ArgumentNullException(nameof(draft));

            lock (_lock)
            {
                var id = _nextItemId++;
                long? traceOrdinal = draft.IsSessionScoped ? id : (long?)null;
                var item = new TelemetryItem(
                    kind: draft.Kind,
                    partitionKey: draft.PartitionKey,
                    rowKey: draft.RowKey,
                    telemetryItemId: id,
                    sessionTraceOrdinal: traceOrdinal,
                    payloadJson: draft.PayloadJson,
                    requiresImmediateFlush: draft.RequiresImmediateFlush,
                    enqueuedAtUtc: DateTime.UtcNow);
                _enqueued.Add(item);
                return item;
            }
        }

        public Task<DrainResult> DrainAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(DrainResult.Empty());

        public void Dispose() { }
    }
}
