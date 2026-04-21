using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Queue-message envelope for the <c>telemetry-index-reconcile</c> queue
    /// (Plan §2.8, §M5.d). Carries all discriminators needed for the queue-triggered
    /// handler (M5.d.3) to fan-out one primary row into the 0–3 applicable index tables,
    /// without needing to re-read the primary row from storage.
    /// <para>
    /// Flat shape (nullable fields per <see cref="SourceKind"/>) deliberately avoids
    /// polymorphic Newtonsoft deserialization — see M5.c.3 finding that abstract-type
    /// round-trips through JSON need bespoke type-handling configuration, which isn't
    /// worth the complexity for a two-variant envelope.
    /// </para>
    /// </summary>
    public sealed class IndexReconcileEnvelope
    {
        /// <summary>Schema version — bump on breaking envelope changes so consumers can reject or migrate.</summary>
        public string EnvelopeVersion { get; set; } = "1";

        /// <summary>Either <c>"Signal"</c> or <c>"DecisionTransition"</c>; drives which nullable fields carry data.</summary>
        public string SourceKind { get; set; } = string.Empty;

        public string TenantId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;

        /// <summary>When the source event happened on the agent clock; primary RK is derived from this.</summary>
        public DateTime OccurredAtUtc { get; set; }

        // ---------- SourceKind="Signal" fields ----------

        /// <summary>Back-ref to <c>Signals</c> primary row (its RK).</summary>
        public long? SessionSignalOrdinal { get; set; }

        public string? SignalKind { get; set; }

        public string? SourceOrigin { get; set; }

        // ---------- SourceKind="DecisionTransition" fields ----------

        /// <summary>Back-ref to <c>DecisionTransitions</c> primary row (its RK).</summary>
        public int? StepIndex { get; set; }

        public string? FromStage { get; set; }
        public string? ToStage { get; set; }

        public bool? Taken { get; set; }
        public bool? IsTerminal { get; set; }

        public string? DeadEndReason { get; set; }

        public string? ClassifierVerdictId { get; set; }
        public string? ClassifierHypothesisLevel { get; set; }
    }
}
