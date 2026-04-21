#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Führt die von einem Reducer-Schritt erzeugten <see cref="DecisionEffect"/>s aus. Plan §2.5 / §2.7b.
    /// <para>
    /// Single side-channel: Reducer bleibt pur, I/O läuft ausschließlich hier.
    /// Fehlerklassen (§2.7b):
    /// <list type="bullet">
    ///   <item><b>Transient</b> (<see cref="DecisionEffectKind.EmitEventTimelineEntry"/>, <see cref="DecisionEffectKind.PersistSnapshot"/>):
    ///       Retry 100/400/1600ms; nach Exhaust Warning, kein Abort.</item>
    ///   <item><b>Kritisch</b> (<see cref="DecisionEffectKind.ScheduleDeadline"/>, <see cref="DecisionEffectKind.CancelDeadline"/>):
    ///       jede Exception → <see cref="EffectRunResult.SessionMustAbort"/>.</item>
    ///   <item><b>Optional</b> (<see cref="DecisionEffectKind.RunClassifier"/>): Exception → Inconclusive-Verdict,
    ///       kein Abort.</item>
    /// </list>
    /// </para>
    /// </summary>
    public interface IEffectRunner
    {
        /// <summary>
        /// Führt alle <paramref name="effects"/> der Reihe nach aus.
        /// <paramref name="stateAfterReduce"/> ist der neue <see cref="DecisionState"/> nach dem
        /// Reduce-Schritt, der die Effekte erzeugt hat (wird für Anti-Loop-Lookup und
        /// Event-Emitter-Kontext gebraucht).
        /// <paramref name="stepOccurredAtUtc"/> wird als <c>OccurredAtUtc</c> der synthetischen
        /// Signals (z.B. ClassifierVerdictIssued) verwendet — so bleibt die Zeitreihenfolge
        /// deterministisch beim Replay.
        /// </summary>
        Task<EffectRunResult> RunAsync(
            IReadOnlyList<DecisionEffect> effects,
            DecisionState stateAfterReduce,
            DateTime stepOccurredAtUtc,
            CancellationToken cancellationToken = default);
    }
}
