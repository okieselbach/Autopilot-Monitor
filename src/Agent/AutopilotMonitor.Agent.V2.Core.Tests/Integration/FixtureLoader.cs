using System;
using System.Collections.Generic;
using System.IO;
using AutopilotMonitor.DecisionCore.Serialization;
using AutopilotMonitor.DecisionCore.Signals;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Integration
{
    /// <summary>
    /// Load a DecisionSignal JSONL fixture from <c>tests/fixtures/enrollment-sessions/</c>.
    /// 1:1-Copy aus <c>DecisionCore.Tests/Harness/FixtureLoader.cs</c> — Plan §2.16 Copy-Not-Share-
    /// Prinzip für V2-Isolation.
    /// <para>
    /// Comment lines starting with '#' and blank lines are ignored.
    /// </para>
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

        /// <summary>
        /// Locate the <c>tests/fixtures/enrollment-sessions</c> directory relative to the test binary.
        /// Walks up from <see cref="AppContext.BaseDirectory"/> looking for the solution file.
        /// </summary>
        public static string FixtureRoot()
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 12; i++)
            {
                if (File.Exists(Path.Combine(dir, "AutopilotMonitor.sln")))
                {
                    return Path.Combine(dir, "tests", "fixtures", "enrollment-sessions");
                }
                var parent = Directory.GetParent(dir)?.FullName;
                if (parent == null || parent == dir) break;
                dir = parent;
            }
            throw new DirectoryNotFoundException(
                "Could not locate repo root (AutopilotMonitor.sln) walking up from " +
                AppContext.BaseDirectory);
        }

        public static IReadOnlyList<DecisionSignal> Load(string filename) =>
            LoadJsonl(Path.Combine(FixtureRoot(), filename));
    }
}
