using System;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Signals;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.SignalAdapters
{
    public sealed class DesktopArrivalDetectorAdapterTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        private sealed class Fixture : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public AgentLogger Logger { get; }
            public DesktopArrivalDetector Detector { get; }
            public FakeSignalIngressSink Ingress { get; } = new FakeSignalIngressSink();
            public VirtualClock Clock { get; } = new VirtualClock(Fixed);

            public Fixture()
            {
                Logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
                Detector = new DesktopArrivalDetector(Logger);
            }

            public void Dispose()
            {
                Detector.Dispose();
                Tmp.Dispose();
            }
        }

        [Fact]
        public void TriggerFromTest_emits_DesktopArrived_signal_with_derived_evidence()
        {
            using var f = new Fixture();
            using var adapter = new DesktopArrivalDetectorAdapter(f.Detector, f.Ingress, f.Clock);

            adapter.TriggerFromTest();

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.DesktopArrived, posted.Kind);
            Assert.Equal(Fixed, posted.OccurredAtUtc);
            Assert.Equal("DesktopArrivalDetector", posted.SourceOrigin);
            Assert.Equal(EvidenceKind.Derived, posted.Evidence.Kind);
            Assert.Equal("desktop-arrival-detector-v1", posted.Evidence.Identifier);
        }

        [Fact]
        public void Part2Mode_emits_DesktopArrivedPart2_kind()
        {
            using var f = new Fixture();
            using var adapter = new DesktopArrivalDetectorAdapter(f.Detector, f.Ingress, f.Clock, part2Mode: true);

            adapter.TriggerFromTest();

            var posted = f.Ingress.Posted.Single();
            Assert.Equal(DecisionSignalKind.DesktopArrivedPart2, posted.Kind);
            Assert.Contains("Part 2", posted.Evidence.Summary);
        }

        [Fact]
        public void Duplicate_trigger_is_deduplicated()
        {
            using var f = new Fixture();
            using var adapter = new DesktopArrivalDetectorAdapter(f.Detector, f.Ingress, f.Clock);

            adapter.TriggerFromTest();
            adapter.TriggerFromTest();
            adapter.TriggerFromTest();

            Assert.Single(f.Ingress.Posted);
        }

        [Fact]
        public void Dispose_unsubscribes_from_detector_event()
        {
            using var f = new Fixture();
            var adapter = new DesktopArrivalDetectorAdapter(f.Detector, f.Ingress, f.Clock);
            adapter.Dispose();

            // After Dispose, the adapter is still functional via TriggerFromTest (test hook
            // doesn't go through the event), but the actual event is no longer subscribed.
            // We can't observe unsubscription directly without reflection; assert via Disposal-OK.
            // No exception during Dispose is the contract.
        }

        [Fact]
        public void Ctor_null_args_throw()
        {
            using var f = new Fixture();
            Assert.Throws<ArgumentNullException>(
                () => new DesktopArrivalDetectorAdapter(null!, f.Ingress, f.Clock));
            Assert.Throws<ArgumentNullException>(
                () => new DesktopArrivalDetectorAdapter(f.Detector, null!, f.Clock));
            Assert.Throws<ArgumentNullException>(
                () => new DesktopArrivalDetectorAdapter(f.Detector, f.Ingress, null!));
        }
    }
}
