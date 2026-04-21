#nullable enable
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Senke für <see cref="SignalIngress"/> nach einem erfolgreichen
    /// <see cref="IDecisionEngine.Reduce"/>-Call. Plan §2.5 / §2.7 / L.1.
    /// <para>
    /// Der Enrollment-Orchestrator (M4.4.5) implementiert diese Abstraktion und übernimmt
    /// Journal-Append, <see cref="IEffectRunner"/>-Invocation und State-Update in einer
    /// Transaktion. SignalIngress kennt diese Schichten nicht.
    /// </para>
    /// <para>
    /// <b>Thread-Modell</b>: <see cref="ApplyStep"/> wird ausschließlich vom Ingress-Worker
    /// aufgerufen (Single-Thread). Implementierungen dürfen das voraussetzen — kein eigenes
    /// Locking nötig. <see cref="CurrentState"/> wird vom selben Worker vor jedem Reduce
    /// gelesen; das ApplyStep-Callback setzt ihn fort.
    /// </para>
    /// </summary>
    public interface IDecisionStepProcessor
    {
        /// <summary>
        /// Aktueller <see cref="DecisionState"/>, den der Worker als Input für den nächsten
        /// Reduce-Call verwendet. Initial typischerweise
        /// <see cref="DecisionState.CreateInitial"/> oder ein wiederhergestellter Snapshot.
        /// </summary>
        DecisionState CurrentState { get; }

        /// <summary>
        /// Angewandten Reducer-Step persistieren. Implementierer:
        /// <list type="number">
        ///   <item>Transition ins Journal appenden (Sofort-Flush)</item>
        ///   <item>Effekte mittels <see cref="IEffectRunner"/> ausführen</item>
        ///   <item><see cref="CurrentState"/> auf <see cref="DecisionStep.NewState"/> fortsetzen</item>
        /// </list>
        /// Darf werfen — Ingress fängt ab und loggt, verstummt aber nicht.
        /// </summary>
        void ApplyStep(DecisionStep step, DecisionSignal signal);
    }
}
