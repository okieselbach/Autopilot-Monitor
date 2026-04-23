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
    public sealed class ProvisioningStatusTrackerAdapterTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        private sealed class Fixture : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public AgentLogger Logger { get; }
            public ProvisioningStatusTracker Tracker { get; }
            public FakeSignalIngressSink Ingress { get; } = new FakeSignalIngressSink();
            public VirtualClock Clock { get; } = new VirtualClock(Fixed);

            public Fixture()
            {
                Logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
                var trackerPost = new InformationalEventPost(new FakeSignalIngressSink(), Clock);
                Tracker = new ProvisioningStatusTracker(
                    sessionId: "S1",
                    tenantId: "T1",
                    post: trackerPost,
                    logger: Logger);
            }

            public void Dispose()
            {
                Tracker.Dispose();
                Tmp.Dispose();
            }
        }

        [Fact]
        public void DeviceSetupComplete_maps_to_DeviceSetupProvisioningComplete_signal()
        {
            using var f = new Fixture();
            using var adapter = new ProvisioningStatusTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerDeviceSetupCompleteFromTest();

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.DeviceSetupProvisioningComplete, posted.Kind);
            Assert.Equal(Fixed, posted.OccurredAtUtc);
            Assert.Equal("ProvisioningStatusTracker", posted.SourceOrigin);
            // Snapshot without real registry data → "unknown"
            Assert.Equal("unknown", posted.Payload!["deviceSetupResolved"]);
        }

        [Fact]
        public void EspFailure_maps_to_EspTerminalFailure_with_distinct_sourceOrigin_from_ShellCore()
        {
            using var f = new Fixture();
            using var adapter = new ProvisioningStatusTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerEspFailureFromTest("DevicePreparation_Failed");

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.EspTerminalFailure, posted.Kind);
            // SourceOrigin distinguishes ShellCore vs. ProvisioningStatus — important for
            // multi-source failure attribution in the journal.
            Assert.Equal("ProvisioningStatusTracker", posted.SourceOrigin);
            Assert.Equal("DevicePreparation_Failed", posted.Payload!["failureType"]);
        }

        [Fact]
        public void EspFailure_empty_type_falls_back_to_unknown()
        {
            using var f = new Fixture();
            using var adapter = new ProvisioningStatusTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerEspFailureFromTest("");

            Assert.Equal("unknown", f.Ingress.Posted[0].Payload!["failureType"]);
        }

        [Fact]
        public void Each_signal_kind_is_deduplicated_independently()
        {
            using var f = new Fixture();
            using var adapter = new ProvisioningStatusTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerDeviceSetupCompleteFromTest();
            adapter.TriggerDeviceSetupCompleteFromTest();   // dedup
            adapter.TriggerEspFailureFromTest("t1");
            adapter.TriggerEspFailureFromTest("t2");   // dedup

            Assert.Equal(2, f.Ingress.Posted.Count);
            Assert.Contains(f.Ingress.Posted, p => p.Kind == DecisionSignalKind.DeviceSetupProvisioningComplete);
            Assert.Contains(f.Ingress.Posted, p => p.Kind == DecisionSignalKind.EspTerminalFailure);
        }

        [Fact]
        public void Ctor_null_args_throw()
        {
            using var f = new Fixture();
            Assert.Throws<ArgumentNullException>(
                () => new ProvisioningStatusTrackerAdapter(null!, f.Ingress, f.Clock));
            Assert.Throws<ArgumentNullException>(
                () => new ProvisioningStatusTrackerAdapter(f.Tracker, null!, f.Clock));
            Assert.Throws<ArgumentNullException>(
                () => new ProvisioningStatusTrackerAdapter(f.Tracker, f.Ingress, null!));
        }
    }
}
