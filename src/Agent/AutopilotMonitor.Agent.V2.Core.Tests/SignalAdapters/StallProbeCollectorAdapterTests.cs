using System;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Signals;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.SignalAdapters
{
    public sealed class StallProbeCollectorAdapterTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        private sealed class Fixture : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public AgentLogger Logger { get; }
            public StallProbeCollector Collector { get; }
            public FakeSignalIngressSink Ingress { get; } = new FakeSignalIngressSink();
            public VirtualClock Clock { get; } = new VirtualClock(Fixed);

            public Fixture()
            {
                Logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
                // Tracker emissions go to a separate throwaway ingress so the adapter assertions
                // on Ingress.Posted stay unpolluted by InformationalEvent pass-through signals.
                var trackerPost = new InformationalEventPost(new FakeSignalIngressSink(), Clock);
                Collector = new StallProbeCollector(
                    sessionId: "S1",
                    tenantId: "T1",
                    post: trackerPost,
                    logger: Logger,
                    thresholdsMinutes: new[] { 2, 5 },
                    traceIndices: new[] { 2, 3, 4 },
                    sources: new[] { "provisioning", "event-logs" },
                    sessionStalledAfterProbeIndex: 4);
            }

            public void Dispose()
            {
                // StallProbeCollector does not implement IDisposable in this copy.
                Tmp.Dispose();
            }
        }

        [Fact]
        public void EspFailure_maps_to_EspTerminalFailure_with_stallprobe_origin()
        {
            using var f = new Fixture();
            using var adapter = new StallProbeCollectorAdapter(f.Collector, f.Ingress, f.Clock);

            adapter.TriggerEspFailureFromTest("AppWorkload_ErrorTerminal");

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.EspTerminalFailure, posted.Kind);
            Assert.Equal("StallProbeCollector", posted.SourceOrigin);
            Assert.Equal("AppWorkload_ErrorTerminal", posted.Payload!["failureType"]);
            Assert.Equal("stall-probe", posted.Payload["detector"]);
            Assert.Equal("AppWorkload_ErrorTerminal", posted.Evidence.DerivationInputs!["terminalReason"]);
        }

        [Fact]
        public void Empty_terminalReason_falls_back_to_unknown()
        {
            using var f = new Fixture();
            using var adapter = new StallProbeCollectorAdapter(f.Collector, f.Ingress, f.Clock);

            adapter.TriggerEspFailureFromTest("");

            Assert.Equal("unknown", f.Ingress.Posted[0].Payload!["failureType"]);
        }

        [Fact]
        public void Duplicate_trigger_is_deduplicated_fire_once()
        {
            using var f = new Fixture();
            using var adapter = new StallProbeCollectorAdapter(f.Collector, f.Ingress, f.Clock);

            adapter.TriggerEspFailureFromTest("reason1");
            adapter.TriggerEspFailureFromTest("reason2");

            Assert.Single(f.Ingress.Posted);
            Assert.Equal("reason1", f.Ingress.Posted[0].Payload!["failureType"]);
        }

        [Fact]
        public void Ctor_null_args_throw()
        {
            using var f = new Fixture();
            Assert.Throws<ArgumentNullException>(() => new StallProbeCollectorAdapter(null!, f.Ingress, f.Clock));
            Assert.Throws<ArgumentNullException>(() => new StallProbeCollectorAdapter(f.Collector, null!, f.Clock));
            Assert.Throws<ArgumentNullException>(() => new StallProbeCollectorAdapter(f.Collector, f.Ingress, null!));
        }
    }
}
