using System.Collections.Generic;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather
{
    /// <summary>
    /// Strategy interface for individual gather rule collector implementations.
    /// </summary>
    public interface IGatherRuleCollector
    {
        string CollectorType { get; }
        Dictionary<string, object> Execute(GatherRule rule, GatherRuleContext context);
    }
}
