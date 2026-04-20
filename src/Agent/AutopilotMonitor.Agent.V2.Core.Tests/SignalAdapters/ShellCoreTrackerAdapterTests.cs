using System;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.SignalAdapters
{
    public sealed class ShellCoreTrackerAdapterTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        private sealed class Fixture : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public AgentLogger Logger { get; }
            public ShellCoreTracker Tracker { get; }
            public FakeSignalIngressSink Ingress { get; } = new FakeSignalIngressSink();
            public VirtualClock Clock { get; } = new VirtualClock(Fixed);

            public Fixture()
            {
                Logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
                Tracker = new ShellCoreTracker(
                    sessionId: "S1",
                    tenantId: "T1",
                    onEventCollected: _ => { },
                    logger: Logger,
                    helloTracker: null);
            }

            public void Dispose()
            {
                Tracker.Dispose();
                Tmp.Dispose();
            }
        }

        [Fact]
        public void FinalizingPhase_maps_to_EspPhaseChanged_with_Finalizing_payload()
        {
            using var f = new Fixture();
            using var adapter = new ShellCoreTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerFinalizingFromTest("shell-core-62404");

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.EspPhaseChanged, posted.Kind);
            Assert.Equal(Fixed, posted.OccurredAtUtc);
            Assert.Equal("ShellCoreTracker", posted.SourceOrigin);
            Assert.Equal(
                EnrollmentPhase.FinalizingSetup.ToString(),
                posted.Payload![SignalPayloadKeys.EspPhase]);
            Assert.Equal("shell-core-62404", posted.Payload["reason"]);
        }

        [Fact]
        public void WhiteGloveCompleted_maps_to_WhiteGloveShellCoreSuccess()
        {
            using var f = new Fixture();
            using var adapter = new ShellCoreTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerWhiteGloveCompletedFromTest();

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.WhiteGloveShellCoreSuccess, posted.Kind);
            Assert.Equal("ShellCoreTracker", posted.SourceOrigin);
            Assert.Equal(EvidenceKind.Derived, posted.Evidence.Kind);
            Assert.Contains("sealing success", posted.Evidence.Summary);
        }

        [Fact]
        public void EspFailure_maps_to_EspTerminalFailure_with_failureType_in_payload()
        {
            using var f = new Fixture();
            using var adapter = new ShellCoreTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerEspFailureFromTest("AccountSetup_Timeout");

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.EspTerminalFailure, posted.Kind);
            Assert.Equal("AccountSetup_Timeout", posted.Payload!["failureType"]);
            Assert.Equal("AccountSetup_Timeout", posted.Evidence.DerivationInputs!["failureType"]);
        }

        [Fact]
        public void EspFailure_empty_type_falls_back_to_unknown()
        {
            using var f = new Fixture();
            using var adapter = new ShellCoreTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerEspFailureFromTest("");

            Assert.Equal("unknown", f.Ingress.Posted[0].Payload!["failureType"]);
        }

        [Fact]
        public void Each_signal_kind_is_deduplicated_independently()
        {
            using var f = new Fixture();
            using var adapter = new ShellCoreTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerFinalizingFromTest("r1");
            adapter.TriggerFinalizingFromTest("r2");   // dedup
            adapter.TriggerWhiteGloveCompletedFromTest();
            adapter.TriggerWhiteGloveCompletedFromTest();   // dedup
            adapter.TriggerEspFailureFromTest("t1");
            adapter.TriggerEspFailureFromTest("t2");   // dedup

            Assert.Equal(3, f.Ingress.Posted.Count);
            Assert.Contains(f.Ingress.Posted, p => p.Kind == DecisionSignalKind.EspPhaseChanged);
            Assert.Contains(f.Ingress.Posted, p => p.Kind == DecisionSignalKind.WhiteGloveShellCoreSuccess);
            Assert.Contains(f.Ingress.Posted, p => p.Kind == DecisionSignalKind.EspTerminalFailure);
        }

        [Fact]
        public void All_three_events_can_fire_in_one_session()
        {
            using var f = new Fixture();
            using var adapter = new ShellCoreTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerFinalizingFromTest("phase-enter");
            adapter.TriggerWhiteGloveCompletedFromTest();
            adapter.TriggerEspFailureFromTest("Timeout");

            Assert.Equal(3, f.Ingress.Posted.Count);
            Assert.Equal(DecisionSignalKind.EspPhaseChanged, f.Ingress.Posted[0].Kind);
            Assert.Equal(DecisionSignalKind.WhiteGloveShellCoreSuccess, f.Ingress.Posted[1].Kind);
            Assert.Equal(DecisionSignalKind.EspTerminalFailure, f.Ingress.Posted[2].Kind);
        }

        [Fact]
        public void Ctor_null_args_throw()
        {
            using var f = new Fixture();
            Assert.Throws<ArgumentNullException>(() => new ShellCoreTrackerAdapter(null!, f.Ingress, f.Clock));
            Assert.Throws<ArgumentNullException>(() => new ShellCoreTrackerAdapter(f.Tracker, null!, f.Clock));
            Assert.Throws<ArgumentNullException>(() => new ShellCoreTrackerAdapter(f.Tracker, f.Ingress, null!));
        }
    }
}
