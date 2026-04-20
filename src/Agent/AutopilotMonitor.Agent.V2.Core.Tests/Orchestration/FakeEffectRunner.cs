#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// Scripted <see cref="IEffectRunner"/> for processor-level unit tests.
    /// Default behavior returns <see cref="EffectRunResult.Empty"/>; use <c>ScriptResult</c>
    /// or <c>ScriptThrow</c> to script per-call responses.
    /// </summary>
    internal sealed class FakeEffectRunner : IEffectRunner
    {
        private readonly Queue<object> _script = new Queue<object>();
        private readonly List<RunCall> _calls = new List<RunCall>();

        public IReadOnlyList<RunCall> Calls => _calls;
        public int CallCount => _calls.Count;

        public FakeEffectRunner ScriptResult(EffectRunResult result, int count = 1)
        {
            for (int i = 0; i < count; i++) _script.Enqueue(result);
            return this;
        }

        public FakeEffectRunner ScriptThrow(Exception ex, int count = 1)
        {
            for (int i = 0; i < count; i++) _script.Enqueue(ex);
            return this;
        }

        public Task<EffectRunResult> RunAsync(
            IReadOnlyList<DecisionEffect> effects,
            DecisionState stateAfterReduce,
            DateTime stepOccurredAtUtc,
            CancellationToken cancellationToken = default)
        {
            _calls.Add(new RunCall(effects, stateAfterReduce, stepOccurredAtUtc));

            if (_script.Count > 0)
            {
                var next = _script.Dequeue();
                if (next is Exception ex) throw ex;
                if (next is EffectRunResult scripted) return Task.FromResult(scripted);
            }
            return Task.FromResult(EffectRunResult.Empty());
        }

        internal sealed class RunCall
        {
            public RunCall(IReadOnlyList<DecisionEffect> effects, DecisionState state, DateTime at)
            {
                Effects = effects;
                State = state;
                OccurredAtUtc = at;
            }

            public IReadOnlyList<DecisionEffect> Effects { get; }
            public DecisionState State { get; }
            public DateTime OccurredAtUtc { get; }
        }
    }
}
