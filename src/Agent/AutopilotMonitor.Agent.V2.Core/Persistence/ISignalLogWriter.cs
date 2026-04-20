#nullable enable
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Signals;

namespace AutopilotMonitor.Agent.V2.Core.Persistence
{
    /// <summary>
    /// Append-only writer for the session SignalLog. Plan §2.7 / L.1.
    /// <para>
    /// Invariants:
    /// <list type="bullet">
    ///   <item>Single-writer (SignalIngress worker thread per §2.1a)</item>
    ///   <item>Sofort-Flush per Append (<c>FileOptions.WriteThrough</c> + <c>Flush(flushToDisk:true)</c>)
    ///         — jeder erfolgreiche Append steht auf Disk bevor die Methode zurückkehrt (L.12)</item>
    ///   <item>Ordinal-Reihenfolge wird vom Aufrufer garantiert (SignalIngress vergibt monoton);
    ///         der Writer validiert sie gegen <see cref="LastOrdinal"/></item>
    /// </list>
    /// </para>
    /// </summary>
    public interface ISignalLogWriter
    {
        /// <summary>
        /// Persist <paramref name="signal"/>. Blockiert bis zum Flush-to-Disk.
        /// Wirft, wenn <c>signal.SessionSignalOrdinal &lt;= LastOrdinal</c> (Monotonie verletzt).
        /// </summary>
        void Append(DecisionSignal signal);

        /// <summary>
        /// Alle persisted Signals in Ordinal-Reihenfolge. Bei korrupter Tail-Zeile wird
        /// bis zur letzten parsbaren Zeile gelesen (Recovery-Regel §2.7 — SignalLog-Tail
        /// kann nach Crash ohne Flush inkonsistent sein; höchster validierbarer Ordinal
        /// ist Wahrheit).
        /// </summary>
        IReadOnlyList<DecisionSignal> ReadAll();

        /// <summary>
        /// Höchster bisher erfolgreich appendete <c>SessionSignalOrdinal</c>; -1 bei leerem Log.
        /// </summary>
        long LastOrdinal { get; }
    }
}
