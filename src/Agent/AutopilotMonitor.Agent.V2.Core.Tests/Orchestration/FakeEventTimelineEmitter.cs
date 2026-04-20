using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// Test fake — records every <see cref="Emit"/> call and optionally throws a scripted
    /// sequence of exceptions (to exercise retry behavior).
    /// </summary>
    internal sealed class FakeEventTimelineEmitter : IEventTimelineEmitter
    {
        private readonly Queue<Func<int, Exception?>> _throwScript = new Queue<Func<int, Exception?>>();
        private readonly List<EmitCall> _calls = new List<EmitCall>();

        public IReadOnlyList<EmitCall> Calls => _calls;
        public int CallCount => _calls.Count;

        /// <summary>Next <paramref name="count"/> calls throw <paramref name="ex"/>.</summary>
        public FakeEventTimelineEmitter ScriptThrow(Exception ex, int count = 1)
        {
            for (int i = 0; i < count; i++) _throwScript.Enqueue(_ => ex);
            return this;
        }

        /// <summary>Next <paramref name="count"/> calls succeed (no-op).</summary>
        public FakeEventTimelineEmitter ScriptOk(int count = 1)
        {
            for (int i = 0; i < count; i++) _throwScript.Enqueue(_ => null);
            return this;
        }

        public void Emit(IReadOnlyDictionary<string, string>? parameters, DecisionState currentState)
        {
            _calls.Add(new EmitCall(parameters, currentState));

            if (_throwScript.Count > 0)
            {
                var maybeEx = _throwScript.Dequeue()(_calls.Count);
                if (maybeEx != null) throw maybeEx;
            }
        }

        internal sealed class EmitCall
        {
            public EmitCall(IReadOnlyDictionary<string, string>? parameters, DecisionState state)
            {
                Parameters = parameters;
                State = state;
            }

            public IReadOnlyDictionary<string, string>? Parameters { get; }
            public DecisionState State { get; }
        }
    }
}
