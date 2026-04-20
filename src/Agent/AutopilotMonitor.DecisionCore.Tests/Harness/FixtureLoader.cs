using System.Collections.Generic;
using System.IO;
using AutopilotMonitor.DecisionCore.Serialization;
using AutopilotMonitor.DecisionCore.Signals;

namespace AutopilotMonitor.DecisionCore.Tests.Harness
{
    /// <summary>
    /// Load a DecisionSignal JSONL fixture from disk. Plan §4 M2 harness integration.
    /// Comment lines starting with '#' and blank lines are ignored.
    /// </summary>
    public static class FixtureLoader
    {
        public static IReadOnlyList<DecisionSignal> LoadJsonl(string path)
        {
            var signals = new List<DecisionSignal>();
            foreach (var rawLine in File.ReadAllLines(path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                signals.Add(SignalSerializer.Deserialize(line));
            }
            return signals;
        }
    }
}
