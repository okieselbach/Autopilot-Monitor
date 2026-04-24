using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.DecisionCore.Engine
{
    /// <summary>
    /// Pure state reconstruction over a persisted signal stream. Codex follow-up #1
    /// (recovery correctness) — agent-side the orchestrator uses this after a
    /// crash to rebuild state from SignalLog when the snapshot is stale or
    /// corrupt; backend-side (Finding #6) the reducer verifier can use the same
    /// call to replay a session offline for determinism checks.
    /// <para>
    /// <b>Contract</b>: pure function of inputs. Same <paramref name="seed"/> +
    /// same ordered signal sequence always produces the same
    /// <see cref="DecisionState"/>. No side effects:
    /// <list type="bullet">
    ///   <item>does NOT append to a journal,</item>
    ///   <item>does NOT invoke the effect runner (no timers armed, no
    ///         telemetry emitted, no classifier reruns),</item>
    ///   <item>does NOT mutate <paramref name="seed"/>.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Ordinal invariant</b>: every replayed signal must have
    /// <c>SessionSignalOrdinal &gt; previous</c>, starting from
    /// <c>seed.LastAppliedSignalOrdinal</c>. The underlying
    /// <c>SignalLogWriter</c> enforces strict monotonicity on append, so a
    /// well-formed log satisfies this automatically. A violation here signals
    /// tampering or a programmer error upstream and is surfaced as
    /// <see cref="InvalidOperationException"/> so the caller can quarantine the
    /// log rather than silently regress state.
    /// </para>
    /// </summary>
    public static class ReducerReplay
    {
        /// <summary>
        /// Fold <paramref name="signals"/> over <paramref name="seed"/> through
        /// <paramref name="engine"/>, returning the final <see cref="DecisionState"/>.
        /// </summary>
        /// <param name="engine">Reducer kernel. Stateless — may be shared across calls.</param>
        /// <param name="seed">Starting state. Typically either <see cref="DecisionState.CreateInitial"/>
        /// (full-stream replay after snapshot quarantine) or a loaded snapshot
        /// (tail replay to catch up to the SignalLog head).</param>
        /// <param name="signals">Ordered signal sequence. Must be strictly monotonic in
        /// <see cref="DecisionSignal.SessionSignalOrdinal"/>; first ordinal must be
        /// &gt; <c>seed.LastAppliedSignalOrdinal</c>.</param>
        /// <returns>The reduced state after applying every signal.</returns>
        /// <exception cref="ArgumentNullException">Any argument is null.</exception>
        /// <exception cref="InvalidOperationException">Ordinal invariant violated
        /// (non-monotonic stream or first ordinal &lt;= seed ordinal).</exception>
        public static DecisionState Replay(
            IDecisionEngine engine,
            DecisionState seed,
            IEnumerable<DecisionSignal> signals)
        {
            if (engine == null) throw new ArgumentNullException(nameof(engine));
            if (seed == null) throw new ArgumentNullException(nameof(seed));
            if (signals == null) throw new ArgumentNullException(nameof(signals));

            var state = seed;
            var lastOrdinal = seed.LastAppliedSignalOrdinal;

            foreach (var signal in signals)
            {
                if (signal == null)
                {
                    throw new InvalidOperationException(
                        "ReducerReplay: encountered null signal in stream.");
                }

                if (signal.SessionSignalOrdinal <= lastOrdinal)
                {
                    throw new InvalidOperationException(
                        $"ReducerReplay: non-monotonic signal ordinal " +
                        $"({signal.SessionSignalOrdinal} <= previous {lastOrdinal}, " +
                        $"kind={signal.Kind}). Log is corrupt or tampered — caller " +
                        $"should quarantine rather than trust this stream.");
                }

                var step = engine.Reduce(state, signal);
                state = step.NewState;
                lastOrdinal = signal.SessionSignalOrdinal;
            }

            return state;
        }
    }
}
