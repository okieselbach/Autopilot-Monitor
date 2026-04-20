using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Orchestration;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>Captures <see cref="IQuarantineSink.TriggerQuarantine"/> calls for assertions.</summary>
    internal sealed class FakeQuarantineSink : IQuarantineSink
    {
        private readonly List<string> _reasons = new List<string>();

        public IReadOnlyList<string> Reasons => _reasons;
        public int CallCount => _reasons.Count;

        public void TriggerQuarantine(string reason) => _reasons.Add(reason);
    }
}
