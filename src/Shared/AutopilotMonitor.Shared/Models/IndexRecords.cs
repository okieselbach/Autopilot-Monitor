using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Index-table record projecting a terminal <see cref="DecisionTransitionRecord"/> onto the
    /// <c>SessionsByTerminal</c> table (Plan §2.8 query matrix, §M5.d). Written async via the
    /// <c>telemetry-index-reconcile</c> queue after the primary <c>DecisionTransitions</c>
    /// row has committed; enables "which sessions ended in {TerminalStage}?" queries.
    /// <para>
    /// PK = <c>{TenantId}_{TerminalStage}</c>, RK = <c>{OccurredAtUtc:yyyyMMddHHmmss}_{SessionId}</c>.
    /// </para>
    /// </summary>
    public sealed class SessionsByTerminalRecord
    {
        public string TenantId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;

        /// <summary>The terminal <c>SessionStage</c> name (e.g. <c>Completed</c>, <c>Failed</c>, <c>WhiteGloveSealed</c>).</summary>
        public string TerminalStage { get; set; } = string.Empty;

        public DateTime OccurredAtUtc { get; set; }

        /// <summary>Back-ref into the primary <c>DecisionTransitions</c> row so queries can hop to full detail.</summary>
        public int StepIndex { get; set; }
    }

    /// <summary>
    /// Index-table record tracking a session's stage history on the <c>SessionsByStage</c> table
    /// (Plan §2.8 query matrix, §M5.d). Written per stage-entering transition; older stage rows
    /// for the same session remain until queue-side or reconciliation logic removes them.
    /// <para>
    /// PK = <c>{TenantId}_{Stage}</c>, RK = <c>{LastUpdatedUtcTicks:D19}_{SessionId}</c> —
    /// ticks zero-padded to 19 digits so lexicographic RK ordering matches numeric time order.
    /// </para>
    /// </summary>
    public sealed class SessionsByStageRecord
    {
        public string TenantId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;

        /// <summary>The <c>SessionStage</c> name the session entered with this row.</summary>
        public string Stage { get; set; } = string.Empty;

        public DateTime LastUpdatedUtc { get; set; }

        /// <summary>Back-ref into the primary <c>DecisionTransitions</c> row that moved the session into this stage.</summary>
        public int StepIndex { get; set; }
    }

    /// <summary>
    /// Index-table record for dead-end transitions on the <c>DeadEndsByReason</c> table
    /// (Plan §2.8 query matrix, §M5.d). Written for <see cref="DecisionTransitionRecord"/>
    /// rows with <c>Taken=false</c> and non-null <c>DeadEndReason</c>.
    /// <para>
    /// PK = <c>{TenantId}_{DeadEndReason}</c>, RK = <c>{OccurredAtUtc:yyyyMMddHHmmss}_{SessionId}_{StepIndex:D6}</c>.
    /// </para>
    /// </summary>
    public sealed class DeadEndsByReasonRecord
    {
        public string TenantId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;

        public string DeadEndReason { get; set; } = string.Empty;

        /// <summary>Back-ref into the primary <c>DecisionTransitions</c> row.</summary>
        public int StepIndex { get; set; }

        public string FromStage { get; set; } = string.Empty;

        /// <summary>The <c>ToStage</c> the transition attempted but was blocked from reaching.</summary>
        public string AttemptedToStage { get; set; } = string.Empty;

        public DateTime OccurredAtUtc { get; set; }
    }

    /// <summary>
    /// Index-table record projecting classifier verdicts onto the
    /// <c>ClassifierVerdictsByIdLevel</c> table (Plan §2.8 query matrix, §M5.d). Written for
    /// <see cref="DecisionTransitionRecord"/> rows carrying a non-null <c>ClassifierVerdictId</c>.
    /// <para>
    /// PK = <c>{TenantId}_{ClassifierId}_{HypothesisLevel}</c>,
    /// RK = <c>{OccurredAtUtc:yyyyMMddHHmmss}_{SessionId}_{StepIndex:D6}</c>.
    /// </para>
    /// </summary>
    public sealed class ClassifierVerdictsByIdLevelRecord
    {
        public string TenantId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;

        public string ClassifierId { get; set; } = string.Empty;

        /// <summary>Hypothesis level name (e.g. <c>Strong</c>, <c>Weak</c>, <c>None</c>, <c>Inconclusive</c>).</summary>
        public string HypothesisLevel { get; set; } = string.Empty;

        /// <summary>Back-ref into the primary <c>DecisionTransitions</c> row.</summary>
        public int StepIndex { get; set; }

        public DateTime OccurredAtUtc { get; set; }
    }

    /// <summary>
    /// Index-table record projecting signals onto the <c>SignalsByKind</c> table
    /// (Plan §2.8 query matrix, §M5.d). Written per <see cref="SignalRecord"/>; enables
    /// cross-session "all signals of kind {SignalKind} for tenant {TenantId}" queries.
    /// <para>
    /// PK = <c>{TenantId}_{SignalKind}</c>,
    /// RK = <c>{OccurredAtUtc:yyyyMMddHHmmss}_{SessionId}_{SessionSignalOrdinal:D10}</c>.
    /// </para>
    /// </summary>
    public sealed class SignalsByKindRecord
    {
        public string TenantId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;

        public string SignalKind { get; set; } = string.Empty;

        /// <summary>Back-ref into the primary <c>Signals</c> row.</summary>
        public long SessionSignalOrdinal { get; set; }

        public DateTime OccurredAtUtc { get; set; }

        public string SourceOrigin { get; set; } = string.Empty;
    }
}
