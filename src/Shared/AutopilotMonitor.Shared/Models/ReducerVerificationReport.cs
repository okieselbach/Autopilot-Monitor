using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Structural health report for a session's persisted SignalLog + DecisionTransitions
    /// journal. Produced by <c>GET /api/sessions/{id}/reducer-verification</c> (Plan §M5,
    /// admin/ops endpoint, not tenant-exposed).
    /// <para>
    /// <b>Scope:</b> this report covers structural invariants that can be checked without
    /// running the reducer — ordinal contiguity, cross-references, ReducerVersion drift, counts.
    /// A full engine replay with per-step diff would require polymorphic deserialisation of
    /// the <c>DecisionSignal.Evidence</c> payload and is a dedicated follow-up.
    /// </para>
    /// </summary>
    public sealed class ReducerVerificationReport
    {
        public string TenantId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;

        public int SignalCount { get; set; }
        public int TransitionCount { get; set; }

        /// <summary>First transition's <c>ReducerVersion</c> (null when no transitions present).</summary>
        public string? StoredReducerVersion { get; set; }

        /// <summary>The current live backend <c>DecisionEngine.ReducerVersion</c>.</summary>
        public string CurrentReducerVersion { get; set; } = string.Empty;

        /// <summary>
        /// True when stored ≠ current — the session was journaled under a different reducer
        /// build. Plan §2.10 calls this out as a known drift signal rather than a bug; the
        /// report surfaces it so ops can decide if replay is still meaningful.
        /// </summary>
        public bool ReducerVersionDrift { get; set; }

        // ---- Signal ordinal contiguity ---------------------------------------------------
        public bool SignalOrdinalsContiguous { get; set; }
        public long SignalOrdinalFirst { get; set; }
        public long SignalOrdinalLast { get; set; }

        // ---- Transition step-index contiguity --------------------------------------------
        public bool StepIndicesContiguous { get; set; }
        public int StepIndexFirst { get; set; }
        public int StepIndexLast { get; set; }

        /// <summary>
        /// Transitions whose <c>SignalOrdinalRef</c> does not match any loaded signal row.
        /// A non-zero count indicates either a corrupted journal or — more likely — truncated
        /// data on the query (the verifier loaded a subset of transitions and the referenced
        /// signals fell outside the slice).
        /// </summary>
        public int OrphanedTransitionCount { get; set; }

        /// <summary>Human-readable issue stream for the Inspector's verification panel.</summary>
        public List<VerificationIssue> Issues { get; set; } = new List<VerificationIssue>();
    }

    public sealed class VerificationIssue
    {
        /// <summary><c>Info</c> / <c>Warning</c> / <c>Error</c>.</summary>
        public string Severity { get; set; } = "Info";

        /// <summary>
        /// Discriminator string: <c>reducer_version_drift</c>, <c>signal_ordinal_gap</c>,
        /// <c>step_index_gap</c>, <c>orphaned_transition</c>, <c>empty_session</c>, …
        /// </summary>
        public string Kind { get; set; } = string.Empty;

        public string Message { get; set; } = string.Empty;
    }
}
