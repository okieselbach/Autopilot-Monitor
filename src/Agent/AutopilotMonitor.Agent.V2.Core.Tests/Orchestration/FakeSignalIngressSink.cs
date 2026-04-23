using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.DecisionCore.Signals;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// Test fake — captures every synthetic signal posted by the EffectRunner.
    /// </summary>
    internal sealed class FakeSignalIngressSink : ISignalIngressSink
    {
        private readonly List<PostedSignal> _posted = new List<PostedSignal>();

        public IReadOnlyList<PostedSignal> Posted => _posted;

        public void Post(
            DecisionSignalKind kind,
            DateTime occurredAtUtc,
            string sourceOrigin,
            Evidence evidence,
            IReadOnlyDictionary<string, string>? payload = null,
            int kindSchemaVersion = 1,
            object? typedPayload = null)
        {
            _posted.Add(new PostedSignal(kind, occurredAtUtc, sourceOrigin, evidence, payload, kindSchemaVersion, typedPayload));
        }

        internal sealed class PostedSignal
        {
            public PostedSignal(
                DecisionSignalKind kind,
                DateTime occurredAtUtc,
                string sourceOrigin,
                Evidence evidence,
                IReadOnlyDictionary<string, string>? payload,
                int kindSchemaVersion,
                object? typedPayload)
            {
                Kind = kind;
                OccurredAtUtc = occurredAtUtc;
                SourceOrigin = sourceOrigin;
                Evidence = evidence;
                Payload = payload;
                KindSchemaVersion = kindSchemaVersion;
                TypedPayload = typedPayload;
            }

            public DecisionSignalKind Kind { get; }
            public DateTime OccurredAtUtc { get; }
            public string SourceOrigin { get; }
            public Evidence Evidence { get; }
            public IReadOnlyDictionary<string, string>? Payload { get; }
            public int KindSchemaVersion { get; }
            public object? TypedPayload { get; }
        }
    }
}
