using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.SignalAdapters
{
    /// <summary>
    /// Plan §4.x M4.4.4 — the <c>OnPatternMatched</c> hook on <see cref="ImeLogTracker"/>
    /// and the <c>WhiteGloveSealingPatternDetected</c> emission it enables on
    /// <see cref="ImeLogTrackerAdapter"/>.
    /// </summary>
    public sealed class ImeLogTrackerAdapterSealingPatternTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        private sealed class Fixture : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public AgentLogger Logger { get; }
            public ImeLogTracker Tracker { get; }
            public FakeSignalIngressSink Ingress { get; } = new FakeSignalIngressSink();
            public VirtualClock Clock { get; } = new VirtualClock(Fixed);

            public Fixture()
            {
                Logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
                Tracker = new ImeLogTracker(
                    logFolder: Tmp.Path,
                    patterns: new List<ImeLogPattern>(),
                    logger: Logger);
            }

            public void Dispose()
            {
                Tracker.Dispose();
                Tmp.Dispose();
            }
        }

        [Fact]
        public void Pattern_match_in_sealing_set_emits_WhiteGloveSealingPatternDetected()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(
                f.Tracker, f.Ingress, f.Clock,
                whiteGloveSealingPatternIds: new[] { "IME-WG-SEAL-1", "IME-WG-SEAL-2" });

            adapter.TriggerPatternMatchedFromTest("IME-WG-SEAL-1");

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.WhiteGloveSealingPatternDetected, posted.Kind);
            Assert.Equal("ImeLogTracker", posted.SourceOrigin);
            Assert.Equal("IME-WG-SEAL-1", posted.Payload![SignalPayloadKeys.ImePatternId]);
            Assert.Equal(EvidenceKind.Derived, posted.Evidence.Kind);
        }

        [Fact]
        public void Pattern_match_NOT_in_sealing_set_does_not_emit()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(
                f.Tracker, f.Ingress, f.Clock,
                whiteGloveSealingPatternIds: new[] { "IME-WG-SEAL-1" });

            adapter.TriggerPatternMatchedFromTest("IME-SOMETHING-ELSE");

            Assert.Empty(f.Ingress.Posted);
        }

        [Fact]
        public void Empty_sealing_pattern_set_disables_emission_entirely()
        {
            // Default ctor — whiteGloveSealingPatternIds null → no emissions (backwards-compat
            // with pre-M4.4.4 M3 behavior).
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerPatternMatchedFromTest("IME-WG-SEAL-1");
            adapter.TriggerPatternMatchedFromTest("IME-ANYTHING");

            Assert.Empty(f.Ingress.Posted);
        }

        [Fact]
        public void Sealing_pattern_emission_is_fire_once_per_session()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(
                f.Tracker, f.Ingress, f.Clock,
                whiteGloveSealingPatternIds: new[] { "IME-WG-SEAL-1", "IME-WG-SEAL-2" });

            adapter.TriggerPatternMatchedFromTest("IME-WG-SEAL-1");
            adapter.TriggerPatternMatchedFromTest("IME-WG-SEAL-1");   // same ID, already posted
            adapter.TriggerPatternMatchedFromTest("IME-WG-SEAL-2");   // different ID, still dedup'd (session-scoped)

            Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.WhiteGloveSealingPatternDetected, f.Ingress.Posted[0].Kind);
        }

        [Fact]
        public void Empty_or_null_patternId_does_not_emit()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(
                f.Tracker, f.Ingress, f.Clock,
                whiteGloveSealingPatternIds: new[] { "IME-WG-SEAL-1" });

            adapter.TriggerPatternMatchedFromTest(string.Empty);
            adapter.TriggerPatternMatchedFromTest(null!);

            Assert.Empty(f.Ingress.Posted);
        }

        [Fact]
        public void Tracker_OnPatternMatched_hook_fires_for_subscribers()
        {
            // Directly verify the hook surface on ImeLogTracker — adapter subscribes via this
            // Action. We simulate the tracker's HandlePatternMatch entry by invoking the
            // property the same way HandlePatternMatch does.
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(
                f.Tracker, f.Ingress, f.Clock,
                whiteGloveSealingPatternIds: new[] { "ANY" });

            // Invoke the hook as if a pattern matched.
            f.Tracker.OnPatternMatched?.Invoke("ANY");

            Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.WhiteGloveSealingPatternDetected, f.Ingress.Posted[0].Kind);
        }

        [Fact]
        public void Previously_wired_OnPatternMatched_is_chain_preserved_and_restored_on_Dispose()
        {
            using var f = new Fixture();
            var chainInvocations = new List<string>();
            f.Tracker.OnPatternMatched = id => chainInvocations.Add("prev:" + id);

            using (var adapter = new ImeLogTrackerAdapter(
                f.Tracker, f.Ingress, f.Clock,
                whiteGloveSealingPatternIds: new[] { "WG" }))
            {
                // Invoking the now-adapter-owned handler should still call the previous one.
                f.Tracker.OnPatternMatched?.Invoke("WG");
                Assert.Contains("prev:WG", chainInvocations);
                Assert.Single(f.Ingress.Posted);
            }

            // After Dispose the previous handler must be restored (no dead callback).
            chainInvocations.Clear();
            f.Tracker.OnPatternMatched?.Invoke("WG-AFTER-DISPOSE");
            Assert.Equal(new[] { "prev:WG-AFTER-DISPOSE" }, chainInvocations);
        }
    }
}
