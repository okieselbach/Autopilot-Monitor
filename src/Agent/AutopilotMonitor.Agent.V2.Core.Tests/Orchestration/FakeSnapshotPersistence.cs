using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Persistence;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// Test fake — in-memory snapshot persistence with scripted exception sequence.
    /// </summary>
    internal sealed class FakeSnapshotPersistence : ISnapshotPersistence
    {
        private readonly Queue<Exception?> _saveScript = new Queue<Exception?>();
        private readonly List<DecisionState> _saved = new List<DecisionState>();

        public IReadOnlyList<DecisionState> Saved => _saved;
        public int SaveCallCount => _saved.Count + _failedSaveCalls;
        private int _failedSaveCalls;

        public FakeSnapshotPersistence ScriptThrow(Exception ex, int count = 1)
        {
            for (int i = 0; i < count; i++) _saveScript.Enqueue(ex);
            return this;
        }

        public FakeSnapshotPersistence ScriptOk(int count = 1)
        {
            for (int i = 0; i < count; i++) _saveScript.Enqueue(null);
            return this;
        }

        public void Save(DecisionState state)
        {
            if (_saveScript.Count > 0)
            {
                var maybeEx = _saveScript.Dequeue();
                if (maybeEx != null)
                {
                    _failedSaveCalls++;
                    throw maybeEx;
                }
            }
            _saved.Add(state);
        }

        public DecisionState? Load() =>
            _saved.Count == 0 ? null : _saved[_saved.Count - 1];

        public void Quarantine(string reason) { /* no-op */ }
    }
}
