using System;
using System.Reflection;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.DecisionCore.Engine
{
    /// <summary>
    /// Kernel stub for the Decision Engine. Plan §2.5 + M1 gate.
    /// <para>
    /// M1 scaffolding — no handler dispatch implemented. <see cref="Reduce"/> throws
    /// <see cref="NotImplementedException"/> so that any accidental runtime call from
    /// the Legacy-Agent or Backend fails loudly during the scaffolding phase (plan:
    /// "Agent + Backend referenzieren, ohne Warnings, keine Call-Sites").
    /// </para>
    /// <para>
    /// M3 replaces this body with the partial-class dispatcher
    /// (<c>DecisionEngine.Classic.cs</c>, <c>.SelfDeploying.cs</c>, <c>.WhiteGlove.cs</c>,
    /// <c>.WhiteGlovePart2.cs</c>, <c>.Shared.cs</c>) and the dispatch table is keyed on
    /// <c>(DecisionSignalKind, KindSchemaVersion)</c>.
    /// </para>
    /// </summary>
    public sealed partial class DecisionEngine : IDecisionEngine
    {
        private static readonly string s_reducerVersion =
            typeof(DecisionEngine).GetTypeInfo().Assembly.GetName().Version?.ToString() ?? "0.0.0.0";

        public string ReducerVersion => s_reducerVersion;

        public DecisionStep Reduce(DecisionState oldState, DecisionSignal signal)
        {
            if (oldState == null)
            {
                throw new ArgumentNullException(nameof(oldState));
            }

            if (signal == null)
            {
                throw new ArgumentNullException(nameof(signal));
            }

            throw new NotImplementedException(
                "DecisionEngine.Reduce is a scaffolding stub (M1). " +
                "Handler implementation lands in M3 (plan §2.5).");
        }
    }
}
