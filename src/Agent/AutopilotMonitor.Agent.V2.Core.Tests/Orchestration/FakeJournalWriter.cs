using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Persistence;
using AutopilotMonitor.DecisionCore.Engine;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// In-memory <see cref="IJournalWriter"/> fake with scripted exceptions.
    /// Mirrors the <see cref="FakeSnapshotPersistence"/> pattern.
    /// </summary>
    internal sealed class FakeJournalWriter : IJournalWriter
    {
        private readonly Queue<Exception?> _appendScript = new Queue<Exception?>();
        private readonly List<DecisionTransition> _appended = new List<DecisionTransition>();
        private int _failedAppends;

        public IReadOnlyList<DecisionTransition> Appended => _appended;
        public int AppendCallCount => _appended.Count + _failedAppends;
        public int LastStepIndex => _appended.Count == 0 ? -1 : _appended[_appended.Count - 1].StepIndex;

        public FakeJournalWriter ScriptThrow(Exception ex, int count = 1)
        {
            for (int i = 0; i < count; i++) _appendScript.Enqueue(ex);
            return this;
        }

        public FakeJournalWriter ScriptOk(int count = 1)
        {
            for (int i = 0; i < count; i++) _appendScript.Enqueue(null);
            return this;
        }

        public void Append(DecisionTransition transition)
        {
            if (transition == null) throw new ArgumentNullException(nameof(transition));

            if (_appendScript.Count > 0)
            {
                var maybeEx = _appendScript.Dequeue();
                if (maybeEx != null)
                {
                    _failedAppends++;
                    throw maybeEx;
                }
            }
            _appended.Add(transition);
        }

        public IReadOnlyList<DecisionTransition> ReadAll() => _appended.ToArray();
    }
}
