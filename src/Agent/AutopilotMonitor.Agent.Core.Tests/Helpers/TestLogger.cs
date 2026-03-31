using System;
using System.IO;
using AutopilotMonitor.Agent.Core.Logging;

namespace AutopilotMonitor.Agent.Core.Tests.Helpers
{
    /// <summary>
    /// Minimal AgentLogger for test use. Writes to a temp directory that is auto-cleaned.
    /// </summary>
    internal static class TestLogger
    {
        private static readonly Lazy<AgentLogger> _instance = new Lazy<AgentLogger>(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "autopilot-test-logs-" + Guid.NewGuid().ToString("N"));
            return new AgentLogger(dir);
        });

        public static AgentLogger Instance => _instance.Value;
    }
}
