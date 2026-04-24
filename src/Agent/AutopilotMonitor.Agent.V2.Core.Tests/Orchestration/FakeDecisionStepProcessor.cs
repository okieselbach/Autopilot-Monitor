using System;
using System.Collections.Generic;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// Test fake for <see cref="IDecisionStepProcessor"/>.
    /// Captures ApplyStep calls, advances <see cref="CurrentState"/> to the step's new state,
    /// and supports scripted exceptions + a blocking handle for back-pressure tests.
    /// </summary>
    internal sealed class FakeDecisionStepProcessor : IDecisionStepProcessor
    {
        private readonly object _lock = new object();
        private readonly Queue<Exception?> _script = new Queue<Exception?>();
        private readonly Queue<EffectRunResult> _resultScript = new Queue<EffectRunResult>();
        private readonly List<ApplyCall> _calls = new List<ApplyCall>();
        private DecisionState _currentState;
        private int _failedApplyCount;

        public FakeDecisionStepProcessor(string sessionId = "S1", string tenantId = "T1")
        {
            _currentState = DecisionState.CreateInitial(sessionId, tenantId);
        }

        /// <summary>Wenn gesetzt, wartet <see cref="ApplyStep"/> darauf bevor es weiterläuft.</summary>
        public ManualResetEventSlim? BlockHandle { get; set; }

        public DecisionState CurrentState
        {
            get { lock (_lock) return _currentState; }
        }

        public int ApplyCallCount
        {
            get { lock (_lock) return _calls.Count + _failedApplyCount; }
        }

        public IReadOnlyList<ApplyCall> Calls
        {
            get { lock (_lock) return _calls.ToArray(); }
        }

        public FakeDecisionStepProcessor ScriptThrow(Exception ex, int count = 1)
        {
            lock (_lock)
            {
                for (int i = 0; i < count; i++) _script.Enqueue(ex);
            }
            return this;
        }

        public FakeDecisionStepProcessor ScriptOk(int count = 1)
        {
            lock (_lock)
            {
                for (int i = 0; i < count; i++) _script.Enqueue(null);
            }
            return this;
        }

        /// <summary>
        /// Script a specific <see cref="EffectRunResult"/> for the next ApplyStep call(s) —
        /// lets tests drive the SignalIngress inline-abort path (Codex post-#50 #B).
        /// </summary>
        public FakeDecisionStepProcessor ScriptResult(EffectRunResult result, int count = 1)
        {
            lock (_lock)
            {
                for (int i = 0; i < count; i++) _resultScript.Enqueue(result);
            }
            return this;
        }

        public EffectRunResult ApplyStep(DecisionStep step, DecisionSignal signal)
        {
            BlockHandle?.Wait();

            lock (_lock)
            {
                if (_script.Count > 0)
                {
                    var ex = _script.Dequeue();
                    if (ex != null)
                    {
                        _failedApplyCount++;
                        throw ex;
                    }
                }
                _calls.Add(new ApplyCall(step, signal));
                _currentState = step.NewState;
                return _resultScript.Count > 0 ? _resultScript.Dequeue() : EffectRunResult.Empty();
            }
        }

        internal sealed class ApplyCall
        {
            public ApplyCall(DecisionStep step, DecisionSignal signal)
            {
                Step = step;
                Signal = signal;
            }

            public DecisionStep Step { get; }
            public DecisionSignal Signal { get; }
        }
    }
}
