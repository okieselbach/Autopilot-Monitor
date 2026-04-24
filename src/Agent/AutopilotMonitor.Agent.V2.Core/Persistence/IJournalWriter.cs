#nullable enable
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Engine;

namespace AutopilotMonitor.Agent.V2.Core.Persistence
{
    /// <summary>
    /// Append-only writer for the Decision-Journal. Plan §2.7 / L.2.
    /// <para>
    /// Invariants analog <see cref="ISignalLogWriter"/>:
    /// <list type="bullet">
    ///   <item>Single-writer (Reducer-Loop)</item>
    ///   <item>Sofort-Flush per Append (L.12)</item>
    ///   <item>StepIndex ist monoton steigend — der Aufrufer stellt das sicher, der Writer validiert</item>
    /// </list>
    /// </para>
    /// </summary>
    public interface IJournalWriter
    {
        /// <summary>
        /// Persist <paramref name="transition"/>. Blockiert bis Flush-to-Disk.
        /// Wirft, wenn <c>transition.StepIndex &lt;= LastStepIndex</c>.
        /// </summary>
        void Append(DecisionTransition transition);

        /// <summary>
        /// Alle persisted Transitions in StepIndex-Reihenfolge. Bei korrupter Tail-Zeile
        /// wird bis zur letzten parsbaren Zeile gelesen.
        /// </summary>
        IReadOnlyList<DecisionTransition> ReadAll();

        /// <summary>
        /// Höchster bisher erfolgreich appendeter <c>StepIndex</c>; -1 bei leerem Journal.
        /// </summary>
        int LastStepIndex { get; }

        /// <summary>
        /// Höchster in irgendeiner persisted Transition vorkommender <c>SessionTraceOrdinal</c>;
        /// -1 bei leerem Journal. Analog <c>ISignalLogWriter.LastTraceOrdinal</c> — wird vom
        /// Orchestrator für das Seeding von <c>SessionTraceOrdinalProvider</c> auf Recovery
        /// verwendet.
        /// </summary>
        long LastTraceOrdinal { get; }

        /// <summary>
        /// Drop all transitions with <c>StepIndex &gt; <paramref name="lastValidStepIndex"/></c>.
        /// Codex follow-up #1 / plan §5.1 — required after <c>ReducerReplay.Replay</c> when the
        /// journal tail is ahead of the replayed state (snapshot stale + partial-write
        /// scenarios leave phantom transitions on disk that would otherwise trigger monotonicity
        /// violations on the next live append).
        /// <para>
        /// Behaviour:
        /// <list type="bullet">
        ///   <item>No-op when the journal is empty or already &lt;= the boundary.</item>
        ///   <item>Dropped suffix lines are moved to <c>.quarantine/&lt;ts&gt;/journal-phantom-tail.jsonl</c>
        ///         as a forensic trail.</item>
        ///   <item>Throws when <paramref name="lastValidStepIndex"/> is &gt; <see cref="LastStepIndex"/>
        ///         (can only truncate backwards) or &lt; -1.</item>
        ///   <item>After return, <see cref="LastStepIndex"/> and <see cref="LastTraceOrdinal"/>
        ///         reflect the truncated file.</item>
        /// </list>
        /// </para>
        /// </summary>
        void TruncateAfter(int lastValidStepIndex);
    }
}
