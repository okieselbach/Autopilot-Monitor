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
    }
}
