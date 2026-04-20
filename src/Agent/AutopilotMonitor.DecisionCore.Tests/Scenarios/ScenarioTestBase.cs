using System;
using System.Collections.Generic;
using System.IO;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.DecisionCore.Tests.Harness;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests.Scenarios
{
    /// <summary>
    /// Shared plumbing for scenario-replay tests. Plan §4 M3 gate.
    /// <para>
    /// Subclasses call <see cref="RunFixture(string, string, string)"/> with a fixture filename
    /// (rooted at <c>tests/fixtures/enrollment-sessions/</c>) and a terminal assertion.
    /// </para>
    /// </summary>
    public abstract class ScenarioTestBase
    {
        /// <summary>Locate the <c>tests/fixtures/enrollment-sessions</c> directory relative to the test binary.</summary>
        protected static string FixtureRoot()
        {
            // bin/<cfg>/net8.0 — walk up until we find the repo root (has AutopilotMonitor.sln).
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

        protected static IReadOnlyList<DecisionSignal> LoadFixture(string filename)
        {
            var path = Path.Combine(FixtureRoot(), filename);
            Assert.True(File.Exists(path), $"Fixture not found: {path}");
            return FixtureLoader.LoadJsonl(path);
        }

        /// <summary>
        /// Run a fixture through the real <see cref="DecisionEngine"/> and return the replay result.
        /// Subclasses then assert on terminal stage / outcome / hypotheses / hash.
        /// </summary>
        protected static ReplayResult RunFixture(string fixtureFilename, string sessionId, string tenantId)
        {
            var signals = LoadFixture(fixtureFilename);
            var harness = new ReplayHarness(new DecisionEngine());
            return harness.Replay(sessionId, tenantId, signals);
        }
    }
}
