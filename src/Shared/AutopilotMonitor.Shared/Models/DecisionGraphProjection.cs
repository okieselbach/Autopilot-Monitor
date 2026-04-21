using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Server-side projection of a session's <see cref="DecisionTransitionRecord"/>s into a
    /// renderable DAG for the Inspector (Plan §M5, §M6). Pre-computed on the backend so the
    /// UI receives one structured shape instead of rebuilding the graph from the raw journal.
    /// </summary>
    public sealed class DecisionGraphProjection
    {
        public string TenantId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;

        /// <summary>Unique stages reached in the session (de-duplicated from Transition From/To stages).</summary>
        public List<DecisionGraphNode> Nodes { get; set; } = new List<DecisionGraphNode>();

        /// <summary>One entry per transition — edges preserve chronological order via <see cref="DecisionGraphEdge.StepIndex"/>.</summary>
        public List<DecisionGraphEdge> Edges { get; set; } = new List<DecisionGraphEdge>();

        /// <summary>Plan §2.10 — lets the UI flag sessions running on an older ReducerVersion than current.</summary>
        public string ReducerVersion { get; set; } = string.Empty;
    }

    /// <summary>
    /// One stage the session visited. Identified by the <see cref="SessionStage"/> enum name so
    /// JSON payloads stay forward-compatible with new stages (string, not int ordinal).
    /// </summary>
    public sealed class DecisionGraphNode
    {
        /// <summary>Stage enum name (e.g. <c>"EspInProgress"</c>). Also used as the graph-node ID.</summary>
        public string Id { get; set; } = string.Empty;

        public bool IsTerminal { get; set; }

        /// <summary>
        /// Outcome label derived from the terminal <see cref="Id"/> — <c>"Succeeded"</c>,
        /// <c>"Failed"</c>, <c>"PausedForPart2"</c>, or <c>null</c> for non-terminal nodes.
        /// The richer termination metadata (<c>TerminationReason</c> + <c>TerminationOutcome</c>
        /// from the <c>enrollment_terminated</c> event in M4.6.β) is <b>not</b> inlined here —
        /// a future revision can enrich terminal nodes by joining the Events table if the UI
        /// needs it. Today's Inspector gets the high-level label.
        /// </summary>
        public string? TerminalOutcome { get; set; }

        /// <summary>Number of edges that target this node (for UI sizing / heat-map rendering).</summary>
        public int VisitCount { get; set; }
    }

    /// <summary>
    /// One reducer step. <see cref="Taken"/> false = dead-end edge (guard blocked the transition)
    /// so the Inspector can render the blocked path in a different style.
    /// </summary>
    public sealed class DecisionGraphEdge
    {
        public int StepIndex { get; set; }
        public string FromStage { get; set; } = string.Empty;
        public string ToStage { get; set; } = string.Empty;
        public string Trigger { get; set; } = string.Empty;
        public bool Taken { get; set; }
        public string? DeadEndReason { get; set; }
        public long SignalOrdinalRef { get; set; }
        public DateTime OccurredAtUtc { get; set; }
        public string? ClassifierVerdictId { get; set; }
        public string? ClassifierHypothesisLevel { get; set; }
    }
}
