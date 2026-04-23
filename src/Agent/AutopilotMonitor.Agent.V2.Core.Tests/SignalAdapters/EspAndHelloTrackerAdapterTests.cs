using System;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.SignalAdapters
{
    public sealed class EspAndHelloTrackerAdapterTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        private sealed class Fixture : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public AgentLogger Logger { get; }
            public EspAndHelloTracker Coordinator { get; }
            public FakeSignalIngressSink Ingress { get; } = new FakeSignalIngressSink();
            public VirtualClock Clock { get; } = new VirtualClock(Fixed);

            public Fixture()
            {
                Logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
                var trackerPost = new InformationalEventPost(new FakeSignalIngressSink(), Clock);
                Coordinator = new EspAndHelloTracker(
                    sessionId: "S1",
                    tenantId: "T1",
                    post: trackerPost,
                    logger: Logger);
            }

            public void Dispose()
            {
                Coordinator.Dispose();
                Tmp.Dispose();
            }
        }

        [Fact]
        public void HelloEvent_emits_HelloResolved_with_outcome_fallback_unknown()
        {
            using var f = new Fixture();
            using var adapter = new EspAndHelloTrackerAdapter(f.Coordinator, f.Ingress, f.Clock);

            adapter.TriggerHelloFromTest(null);

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.HelloResolved, posted.Kind);
            Assert.Equal("EspAndHelloTracker", posted.SourceOrigin);
            Assert.Equal("unknown", posted.Payload![SignalPayloadKeys.HelloOutcome]);
        }

        [Fact]
        public void Part2Mode_Hello_emits_HelloResolvedPart2()
        {
            using var f = new Fixture();
            using var adapter = new EspAndHelloTrackerAdapter(f.Coordinator, f.Ingress, f.Clock, part2Mode: true);

            adapter.TriggerHelloFromTest("completed");

            Assert.Equal(DecisionSignalKind.HelloResolvedPart2, f.Ingress.Posted[0].Kind);
            Assert.Equal("completed", f.Ingress.Posted[0].Payload![SignalPayloadKeys.HelloOutcome]);
        }

        [Fact]
        public void FinalizingEvent_emits_EspPhaseChanged_with_FinalizingSetup_payload()
        {
            using var f = new Fixture();
            using var adapter = new EspAndHelloTrackerAdapter(f.Coordinator, f.Ingress, f.Clock);

            adapter.TriggerFinalizingFromTest("62404");

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.EspPhaseChanged, posted.Kind);
            Assert.Equal(EnrollmentPhase.FinalizingSetup.ToString(), posted.Payload![SignalPayloadKeys.EspPhase]);
            Assert.Equal("62404", posted.Payload["reason"]);
        }

        [Fact]
        public void WhiteGloveEvent_emits_WhiteGloveShellCoreSuccess()
        {
            using var f = new Fixture();
            using var adapter = new EspAndHelloTrackerAdapter(f.Coordinator, f.Ingress, f.Clock);

            adapter.TriggerWhiteGloveFromTest();

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.WhiteGloveShellCoreSuccess, posted.Kind);
        }

        [Fact]
        public void EspFailureEvent_emits_EspTerminalFailure_with_merged_subSource_annotation()
        {
            using var f = new Fixture();
            using var adapter = new EspAndHelloTrackerAdapter(f.Coordinator, f.Ingress, f.Clock);

            adapter.TriggerEspFailureFromTest("CoordinatorMergedFailure");

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.EspTerminalFailure, posted.Kind);
            Assert.Equal("CoordinatorMergedFailure", posted.Payload!["failureType"]);
            Assert.Contains("merged", posted.Evidence.DerivationInputs!["subSource"]);
        }

        [Fact]
        public void DeviceSetupCompleteEvent_emits_DeviceSetupProvisioningComplete_with_snapshot_fallback()
        {
            using var f = new Fixture();
            using var adapter = new EspAndHelloTrackerAdapter(f.Coordinator, f.Ingress, f.Clock);

            adapter.TriggerDeviceSetupCompleteFromTest();

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.DeviceSetupProvisioningComplete, posted.Kind);
            Assert.Equal("unknown", posted.Payload!["deviceSetupResolved"]);
        }

        [Fact]
        public void All_five_signal_kinds_can_fire_in_one_session_each_fire_once()
        {
            using var f = new Fixture();
            using var adapter = new EspAndHelloTrackerAdapter(f.Coordinator, f.Ingress, f.Clock);

            adapter.TriggerFinalizingFromTest("r1");
            adapter.TriggerFinalizingFromTest("r2");   // dedup

            adapter.TriggerWhiteGloveFromTest();
            adapter.TriggerWhiteGloveFromTest();   // dedup

            adapter.TriggerHelloFromTest("completed");
            adapter.TriggerHelloFromTest("timeout");   // dedup

            adapter.TriggerEspFailureFromTest("X");
            adapter.TriggerEspFailureFromTest("Y");   // dedup

            adapter.TriggerDeviceSetupCompleteFromTest();
            adapter.TriggerDeviceSetupCompleteFromTest();   // dedup

            Assert.Equal(5, f.Ingress.Posted.Count);
            var kinds = f.Ingress.Posted.Select(p => p.Kind).ToList();
            Assert.Contains(DecisionSignalKind.EspPhaseChanged, kinds);
            Assert.Contains(DecisionSignalKind.WhiteGloveShellCoreSuccess, kinds);
            Assert.Contains(DecisionSignalKind.HelloResolved, kinds);
            Assert.Contains(DecisionSignalKind.EspTerminalFailure, kinds);
            Assert.Contains(DecisionSignalKind.DeviceSetupProvisioningComplete, kinds);
        }

        [Fact]
        public void Ctor_null_args_throw()
        {
            using var f = new Fixture();
            Assert.Throws<ArgumentNullException>(() => new EspAndHelloTrackerAdapter(null!, f.Ingress, f.Clock));
            Assert.Throws<ArgumentNullException>(() => new EspAndHelloTrackerAdapter(f.Coordinator, null!, f.Clock));
            Assert.Throws<ArgumentNullException>(() => new EspAndHelloTrackerAdapter(f.Coordinator, f.Ingress, null!));
        }
    }
}
