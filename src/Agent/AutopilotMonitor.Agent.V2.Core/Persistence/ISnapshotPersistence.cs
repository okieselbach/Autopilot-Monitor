#nullable enable
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.Agent.V2.Core.Persistence
{
    /// <summary>
    /// Snapshot cache for fast start. Plan §2.7 / L.3.
    /// <para>
    /// Snapshot ist Cache, nicht Wahrheit — bei Inkonsistenz oder Korruption wird er
    /// verworfen, nicht repariert (L.3, §2.7c). Der Orchestrator ruft bei Mismatch
    /// <see cref="Quarantine"/> auf und rekonstruiert via
    /// <c>AutopilotMonitor.DecisionCore.Engine.ReducerReplay.Replay</c> aus dem SignalLog.
    /// Ein gültiges Snapshot dient als Seed; ReducerReplay spielt nur den
    /// SignalLog-Tail ab <c>LastAppliedSignalOrdinal</c> darauf (Codex follow-up #1,
    /// Phase 2).
    /// </para>
    /// </summary>
    public interface ISnapshotPersistence
    {
        /// <summary>
        /// Persist <paramref name="state"/>. Atomar (write-temp + rename). Sofort-Flush.
        /// </summary>
        void Save(DecisionState state);

        /// <summary>
        /// Lädt das persistierte Snapshot oder <c>null</c> wenn:
        /// (a) keine Datei vorhanden,
        /// (b) Checksum-Mismatch (Snapshot wird dabei nicht gelöscht — Caller entscheidet
        /// via <see cref="Quarantine"/>).
        /// </summary>
        DecisionState? Load();

        /// <summary>
        /// Verschiebt das aktuelle Snapshot nach <c>.quarantine/{timestamp}/</c> (Plan §2.7
        /// Sonderfall 2). Nach Quarantine ist das Snapshot weg; beim nächsten <see cref="Load"/>
        /// kommt <c>null</c>. <paramref name="reason"/> wird als Sidecar-Textfile mitgeschrieben.
        /// </summary>
        void Quarantine(string reason);
    }
}
