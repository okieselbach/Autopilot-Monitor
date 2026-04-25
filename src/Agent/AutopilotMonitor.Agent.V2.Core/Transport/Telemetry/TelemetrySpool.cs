#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AutopilotMonitor.DecisionCore.Engine;

namespace AutopilotMonitor.Agent.V2.Core.Transport.Telemetry
{
    /// <summary>
    /// Disk-backed <see cref="ITelemetrySpool"/> — append-only JSONL + separater Cursor-File.
    /// Plan §2.7a / L.12 Sofort-Flush.
    /// <para>
    /// <b>Layout auf Disk</b>:
    /// <list type="bullet">
    ///   <item><c>&lt;dir&gt;/spool.jsonl</c> — eine <see cref="TelemetryItem"/>-Zeile pro enqueue, append-only</item>
    ///   <item><c>&lt;dir&gt;/upload-cursor.json</c> — <c>{ "LastUploadedItemId": N }</c>, atomar geschrieben</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Recovery</b>: beim Konstruktor werden beide Files gescannt. <c>LastAssignedItemId</c> = max
    /// <c>TelemetryItemId</c> in spool.jsonl (ab der letzten parsbaren Zeile); <c>LastUploadedItemId</c>
    /// = Wert aus cursor-File (oder -1 bei Fehler).
    /// </para>
    /// </summary>
    public sealed class TelemetrySpool : ITelemetrySpool
    {
        private readonly string _spoolPath;
        private readonly UploadCursorPersistence _cursor;
        private readonly IClock _clock;
        private readonly Logging.AgentLogger? _logger;
        private readonly object _lock = new object();

        private long _lastAssignedItemId = -1;
        private long _lastUploadedItemId = -1;

        public TelemetrySpool(string directoryPath, IClock clock, Logging.AgentLogger? logger = null)
        {
            if (string.IsNullOrEmpty(directoryPath))
            {
                throw new ArgumentException("DirectoryPath is mandatory.", nameof(directoryPath));
            }

            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _logger = logger;

            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            _spoolPath = Path.Combine(directoryPath, "spool.jsonl");
            _cursor = new UploadCursorPersistence(Path.Combine(directoryPath, "upload-cursor.json"));

            _lastAssignedItemId = ScanLastAssignedItemId();
            _lastUploadedItemId = _cursor.Load();
        }

        public long LastAssignedItemId
        {
            get { lock (_lock) return _lastAssignedItemId; }
        }

        public long LastUploadedItemId
        {
            get { lock (_lock) return _lastUploadedItemId; }
        }

        public event EventHandler? ImmediateFlushRequested;

        public TelemetryItem Enqueue(TelemetryItemDraft draft)
        {
            if (draft == null) throw new ArgumentNullException(nameof(draft));

            TelemetryItem item;
            lock (_lock)
            {
                var itemId = _lastAssignedItemId + 1;
                long? traceOrdinal = draft.IsSessionScoped ? itemId : (long?)null;

                item = new TelemetryItem(
                    kind: draft.Kind,
                    partitionKey: draft.PartitionKey,
                    rowKey: draft.RowKey,
                    telemetryItemId: itemId,
                    sessionTraceOrdinal: traceOrdinal,
                    payloadJson: draft.PayloadJson,
                    requiresImmediateFlush: draft.RequiresImmediateFlush,
                    enqueuedAtUtc: _clock.UtcNow);

                var line = TelemetryItemSerializer.Serialize(item);
                var bytes = Encoding.UTF8.GetBytes(line + "\n");

                using (var fs = new FileStream(
                    _spoolPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 4096,
                    options: FileOptions.WriteThrough))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(flushToDisk: true);
                }

                _lastAssignedItemId = itemId;
                // Plan §5 Fix 5 — spool-cadence logging. PR3-A2: VERBOSE (was DEBUG)
                // because per-item logging hits ~600 lines per session and floods the log
                // at troubleshoot level. Activate only at micro-repro level.
                _logger?.Verbose(
                    $"TelemetrySpool: enqueued itemId={itemId} kind={item.Kind} immediate={item.RequiresImmediateFlush} pending={itemId - _lastUploadedItemId}.");
            }

            // Fire outside the lock — subscribers must not be able to re-enter Enqueue or
            // hold the write path while waking the drain loop. Swallow handler exceptions:
            // a buggy subscriber must not break telemetry persistence.
            if (draft.RequiresImmediateFlush)
            {
                try { ImmediateFlushRequested?.Invoke(this, EventArgs.Empty); }
                catch (Exception ex)
                {
                    _logger?.Warning($"TelemetrySpool: ImmediateFlushRequested handler threw: {ex.Message}");
                }
            }

            return item;
        }

        public IReadOnlyList<TelemetryItem> Peek(int max)
        {
            if (max <= 0) return Array.Empty<TelemetryItem>();

            lock (_lock)
            {
                if (!File.Exists(_spoolPath)) return Array.Empty<TelemetryItem>();

                var pending = new List<TelemetryItem>();
                foreach (var line in File.ReadAllLines(_spoolPath, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    TelemetryItem parsed;
                    try
                    {
                        parsed = TelemetryItemSerializer.Deserialize(line);
                    }
                    catch
                    {
                        // Corrupt tail (crash mid-append) — stop reading, the items before
                        // are still valid. Orchestrator will drain them; the unparsable
                        // tail stays in the file but is never Peek-returned.
                        break;
                    }

                    if (parsed.TelemetryItemId <= _lastUploadedItemId) continue;

                    pending.Add(parsed);
                    if (pending.Count >= max) break;
                }

                return pending;
            }
        }

        public void MarkUploaded(long upToItemIdInclusive)
        {
            lock (_lock)
            {
                if (upToItemIdInclusive <= _lastUploadedItemId) return;   // idempotency — never regress
                if (upToItemIdInclusive > _lastAssignedItemId)
                {
                    throw new InvalidOperationException(
                        $"Cannot mark uploaded beyond last-assigned id (requested={upToItemIdInclusive}, lastAssigned={_lastAssignedItemId}).");
                }

                _cursor.Save(upToItemIdInclusive);
                _lastUploadedItemId = upToItemIdInclusive;
            }
        }

        private long ScanLastAssignedItemId()
        {
            if (!File.Exists(_spoolPath)) return -1;

            long highest = -1;
            foreach (var line in File.ReadAllLines(_spoolPath, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var item = TelemetryItemSerializer.Deserialize(line);
                    if (item.TelemetryItemId > highest) highest = item.TelemetryItemId;
                }
                catch
                {
                    break;
                }
            }
            return highest;
        }
    }
}
