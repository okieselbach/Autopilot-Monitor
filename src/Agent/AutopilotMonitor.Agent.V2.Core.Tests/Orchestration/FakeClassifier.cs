using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Classifiers;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// Test fake <see cref="IClassifier"/> — returns a canned verdict or throws on demand.
    /// </summary>
    internal sealed class FakeClassifier : IClassifier
    {
        public FakeClassifier(string id, Func<object, ClassifierVerdict> classifyFn)
        {
            Id = id;
            _classifyFn = classifyFn;
            SnapshotType = typeof(object);
        }

        private readonly Func<object, ClassifierVerdict> _classifyFn;

        public string Id { get; }
        public Type SnapshotType { get; }
        public int CallCount { get; private set; }

        public ClassifierVerdict Classify(object snapshot)
        {
            CallCount++;
            return _classifyFn(snapshot);
        }

        public static ClassifierVerdict Verdict(string id, HypothesisLevel level, string inputHash, int score = 50) =>
            new ClassifierVerdict(
                classifierId: id,
                level: level,
                score: score,
                contributingFactors: Array.Empty<string>(),
                reason: "fake",
                inputHash: inputHash);
    }

    internal sealed class HashSnapshot : IClassifierSnapshot
    {
        private readonly string _hash;
        public HashSnapshot(string hash) => _hash = hash;
        public string ComputeInputHash() => _hash;
    }
}
