#nullable enable
namespace AutopilotMonitor.Agent.V2.Core.Transport.Telemetry
{
    /// <summary>
    /// TRACE-H1 — describes a poison batch the drain skipped (a non-transient, non-auth upload
    /// failure). Carried by <see cref="TelemetryUploadOrchestrator.BatchPoisoned"/> so the
    /// orchestrator can surface a <c>telemetry_upload_poisoned</c> timeline event.
    /// </summary>
    /// <summary>How the dropped item(s) were determined to be un-sendable.</summary>
    public enum PoisonKind
    {
        /// <summary>A lone item the backend rejected with 413 even on its own (locally-provable).</summary>
        Oversize,
        /// <summary>The backend explicitly named these RowKeys as poison in a 4xx body (P1).</summary>
        BackendRejected,
    }

    public sealed class PoisonReport
    {
        public PoisonReport(int itemCount, long throughItemId, string? reason, PoisonKind kind)
        {
            ItemCount = itemCount;
            ThroughItemId = throughItemId;
            Reason = reason;
            Kind = kind;
        }

        /// <summary>How the drop was decided (P3 — keeps forensics from mislabeling explicit poison as "oversized").</summary>
        public PoisonKind Kind { get; }

        /// <summary>Number of spool items in the skipped batch.</summary>
        public int ItemCount { get; }

        /// <summary>The <c>TelemetryItemId</c> the cursor was advanced to (inclusive) to skip the batch.</summary>
        public long ThroughItemId { get; }

        /// <summary>The backend rejection reason (e.g. "http 413: ..."), if known.</summary>
        public string? Reason { get; }
    }
}
