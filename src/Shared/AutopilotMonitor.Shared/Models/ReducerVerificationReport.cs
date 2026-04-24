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

        // ---- Semantic replay (Codex follow-up #6) ----------------------------------------

        /// <summary>
        /// True when the verifier re-played the persisted signal stream through the live
        /// backend <c>DecisionEngine</c> and compared the produced transitions to the stored
        /// journal. False when the replay was skipped — see <see cref="SemanticReplaySkipReason"/>.
        /// </summary>
        public bool SemanticReplayPerformed { get; set; }

        /// <summary>
        /// Discriminator when <see cref="SemanticReplayPerformed"/> is false. Known values:
        /// <c>"empty_session"</c>, <c>"reducer_version_drift"</c>, <c>"non_contiguous_signal_ordinals"</c>,
        /// <c>"non_contiguous_step_indices"</c>, <c>"deserialization_failure"</c>.
        /// </summary>
        public string? SemanticReplaySkipReason { get; set; }

        /// <summary>
        /// True when the replayed <see cref="DecisionCore.State.DecisionState.Stage"/> matches
        /// the <c>ToStage</c> of the last stored transition. Only meaningful when
        /// <see cref="SemanticReplayPerformed"/> is true.
        /// </summary>
        public bool SemanticReplayFinalStageMatches { get; set; }

        /// <summary>The stage the replay arrived at (stringified <c>SessionStage</c>); null on skip.</summary>
        public string? ReplayedFinalStage { get; set; }

        /// <summary>
        /// Number of positions where the replayed transition diverged from the stored one on
        /// the compared fields (Trigger, FromStage, ToStage, Taken, DeadEndReason, StepIndex).
        /// 0 means perfect agreement. Individual divergences are emitted as
        /// <c>replay_divergence</c> <see cref="VerificationIssue"/>s up to a cap of 20.
        /// </summary>
        public int TransitionDivergenceCount { get; set; }

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
