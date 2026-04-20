using System;
using System.Collections.Generic;
using System.Linq;
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
    public sealed class ImeLogTrackerAdapterTests
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
        public void EspPhaseChanged_first_phase_emits_signal_with_phase_in_payload()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerEspPhaseFromTest("DeviceSetup");

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.EspPhaseChanged, posted.Kind);
            Assert.Equal("ImeLogTracker", posted.SourceOrigin);
            Assert.Equal("DeviceSetup", posted.Payload![SignalPayloadKeys.EspPhase]);
        }

        [Fact]
        public void EspPhaseChanged_same_phase_repeated_is_deduped_idempotency()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerEspPhaseFromTest("DeviceSetup");
            adapter.TriggerEspPhaseFromTest("DeviceSetup");
            adapter.TriggerEspPhaseFromTest("DeviceSetup");

            Assert.Single(f.Ingress.Posted);
        }

        [Fact]
        public void EspPhaseChanged_distinct_phases_emit_separate_signals()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerEspPhaseFromTest("DeviceSetup");
            adapter.TriggerEspPhaseFromTest("AccountSetup");
            adapter.TriggerEspPhaseFromTest("AccountSetup");  // dedup
            adapter.TriggerEspPhaseFromTest("FinalizingSetup");

            Assert.Equal(3, f.Ingress.Posted.Count);
            Assert.Equal("DeviceSetup", f.Ingress.Posted[0].Payload![SignalPayloadKeys.EspPhase]);
            Assert.Equal("AccountSetup", f.Ingress.Posted[1].Payload![SignalPayloadKeys.EspPhase]);
            Assert.Equal("FinalizingSetup", f.Ingress.Posted[2].Payload![SignalPayloadKeys.EspPhase]);
        }

        [Fact]
        public void EspPhaseChanged_null_or_empty_phase_is_skipped()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerEspPhaseFromTest(null!);
            adapter.TriggerEspPhaseFromTest("");

            Assert.Empty(f.Ingress.Posted);
        }

        [Fact]
        public void UserSessionCompleted_emits_ImeUserSessionCompleted_fire_once()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerUserSessionCompletedFromTest();
            adapter.TriggerUserSessionCompletedFromTest();

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.ImeUserSessionCompleted, posted.Kind);
        }

        [Theory]
        [InlineData(AppInstallationState.Installed, DecisionSignalKind.AppInstallCompleted)]
        [InlineData(AppInstallationState.Skipped, DecisionSignalKind.AppInstallCompleted)]
        [InlineData(AppInstallationState.Postponed, DecisionSignalKind.AppInstallCompleted)]
        [InlineData(AppInstallationState.Error, DecisionSignalKind.AppInstallFailed)]
        public void AppStateChange_to_terminal_state_emits_correct_signal_kind(
            AppInstallationState newState, DecisionSignalKind expectedKind)
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            var app = new AppPackageState($"app-{newState}", listPos: 0);

            adapter.TriggerAppStateFromTest(app, AppInstallationState.Installing, newState);

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(expectedKind, posted.Kind);
            Assert.Equal($"app-{newState}", posted.Payload!["appId"]);
            Assert.Equal(newState.ToString(), posted.Payload["newState"]);
        }

        [Fact]
        public void AppStateChange_to_non_terminal_state_does_not_emit()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            var app = new AppPackageState("app-1", 0);

            adapter.TriggerAppStateFromTest(app, AppInstallationState.Unknown, AppInstallationState.InProgress);
            adapter.TriggerAppStateFromTest(app, AppInstallationState.InProgress, AppInstallationState.Downloading);
            adapter.TriggerAppStateFromTest(app, AppInstallationState.Downloading, AppInstallationState.Installing);

            Assert.Empty(f.Ingress.Posted);
        }

        [Fact]
        public void AppStateChange_terminal_per_app_is_fire_once()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            var app = new AppPackageState("app-1", 0);

            adapter.TriggerAppStateFromTest(app, AppInstallationState.Installing, AppInstallationState.Installed);
            // Even if the tracker re-fires (shouldn't happen, but defend) — still one post.
            adapter.TriggerAppStateFromTest(app, AppInstallationState.Installed, AppInstallationState.Error);

            Assert.Single(f.Ingress.Posted);
        }

        [Fact]
        public void AppStateChange_different_apps_emit_independently()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerAppStateFromTest(new AppPackageState("a", 0), AppInstallationState.Installing, AppInstallationState.Installed);
            adapter.TriggerAppStateFromTest(new AppPackageState("b", 1), AppInstallationState.Installing, AppInstallationState.Error);
            adapter.TriggerAppStateFromTest(new AppPackageState("c", 2), AppInstallationState.Installing, AppInstallationState.Skipped);

            Assert.Equal(3, f.Ingress.Posted.Count);
            Assert.Contains(f.Ingress.Posted, p => p.Payload!["appId"] == "a" && p.Kind == DecisionSignalKind.AppInstallCompleted);
            Assert.Contains(f.Ingress.Posted, p => p.Payload!["appId"] == "b" && p.Kind == DecisionSignalKind.AppInstallFailed);
            Assert.Contains(f.Ingress.Posted, p => p.Payload!["appId"] == "c" && p.Kind == DecisionSignalKind.AppInstallCompleted);
        }

        [Fact]
        public void AppStateChange_null_app_is_ignored()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerAppStateFromTest(null!, AppInstallationState.Installing, AppInstallationState.Installed);

            Assert.Empty(f.Ingress.Posted);
        }

        [Fact]
        public void Adapter_preserves_prior_Action_handlers_chain_invoke()
        {
            using var f = new Fixture();
            int priorEspCalls = 0;
            int priorUserCalls = 0;
            f.Tracker.OnEspPhaseChanged = _ => priorEspCalls++;
            f.Tracker.OnUserSessionCompleted = () => priorUserCalls++;

            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            // Call through the tracker's Action (simulating what the tracker would do).
            f.Tracker.OnEspPhaseChanged("DeviceSetup");
            f.Tracker.OnUserSessionCompleted();

            Assert.Equal(1, priorEspCalls);
            Assert.Equal(1, priorUserCalls);
            Assert.Equal(2, f.Ingress.Posted.Count);
        }

        [Fact]
        public void Dispose_restores_prior_Action_handlers()
        {
            using var f = new Fixture();
            int priorCalls = 0;
            Action<string> priorAction = _ => priorCalls++;
            f.Tracker.OnEspPhaseChanged = priorAction;

            var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            Assert.NotSame(priorAction, f.Tracker.OnEspPhaseChanged);

            adapter.Dispose();
            Assert.Same(priorAction, f.Tracker.OnEspPhaseChanged);
        }

        [Fact]
        public void Ctor_null_args_throw()
        {
            using var f = new Fixture();
            Assert.Throws<ArgumentNullException>(() => new ImeLogTrackerAdapter(null!, f.Ingress, f.Clock));
            Assert.Throws<ArgumentNullException>(() => new ImeLogTrackerAdapter(f.Tracker, null!, f.Clock));
            Assert.Throws<ArgumentNullException>(() => new ImeLogTrackerAdapter(f.Tracker, f.Ingress, null!));
        }
    }
}
