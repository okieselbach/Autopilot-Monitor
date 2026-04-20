using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.DecisionCore.Engine
{
    /// <summary>
    /// Pure reducer interface. Plan §2.5 / L.2 / L.3.
    /// <para>
    /// <b>Contract</b>: <paramref name="oldState"/> must be treated as immutable and is never
    /// modified. The returned <see cref="DecisionStep.NewState"/> is a fresh instance.
    /// Signal-log determinism (L.5): the same persisted signal stream always produces the
    /// same journal and the same terminal outcome.
    /// </para>
    /// </summary>
    public interface IDecisionEngine
    {
        /// <summary>ReducerVersion stamp written into each journal transition. Drift-detection in §2.10.</summary>
        string ReducerVersion { get; }

        /// <summary>
        /// Apply a single signal to the state and return (newState, transition, effects).
        /// Stubbed in M1; full handler dispatch (partial classes §2.5) comes online in M3.
        /// </summary>
        DecisionStep Reduce(DecisionState oldState, DecisionSignal signal);
    }
}
