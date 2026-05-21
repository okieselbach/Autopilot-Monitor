using System;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using SharedEventTypes = AutopilotMonitor.Shared.Constants.EventTypes;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.SignalAdapters
{
    public sealed class RealmJoinWatcherAdapterTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);

        private sealed class Fixture : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public AgentLogger Logger { get; }
            public RealmJoinWatcher Watcher { get; }
            public FakeSignalIngressSink Ingress { get; } = new FakeSignalIngressSink();
            public VirtualClock Clock { get; } = new VirtualClock(Fixed);

            public Fixture()
            {
                Logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
                Watcher = new RealmJoinWatcher(Logger);
            }

            public void Dispose()
            {
                Watcher.Dispose();
                Tmp.Dispose();
            }
        }

        [Fact]
        public void TriggerDetectedFromTest_emits_RealmJoinDetected_signal_and_informational_event()
        {
            using var f = new Fixture();
            using var adapter = new RealmJoinWatcherAdapter(f.Watcher, f.Ingress, f.Clock);

            adapter.TriggerDetectedFromTest(phase: 100);

            // 1) Decision signal that mutates engine state.
            var decision = f.Ingress.Posted.Single(p => p.Kind == DecisionSignalKind.RealmJoinDetected);
            Assert.Equal("RealmJoinWatcher", decision.SourceOrigin);
            Assert.Equal("100", decision.Payload![DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase]);

            // 2) Dual-emit informational event so the timeline shows realmjoin_detected.
            var info = f.Ingress.Posted.Single(p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == SharedEventTypes.RealmJoinDetected);
            Assert.Equal("RealmJoinWatcher", info.Payload![SignalPayloadKeys.Source]);
            Assert.Equal("true", info.Payload[SignalPayloadKeys.ImmediateUpload]);
            Assert.Equal("100", info.Payload["deploymentPhase"]);
            Assert.Contains("HKLM", info.Payload["registryKey"]);
        }

        [Fact]
        public void TriggerResolvedFromTest_emits_RealmJoinResolved_signal_and_informational_event()
        {
            using var f = new Fixture();
            using var adapter = new RealmJoinWatcherAdapter(f.Watcher, f.Ingress, f.Clock);

            adapter.TriggerResolvedFromTest(phase: 110);

            var decision = f.Ingress.Posted.Single(p => p.Kind == DecisionSignalKind.RealmJoinResolved);
            Assert.Equal("110", decision.Payload![DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase]);

            var info = f.Ingress.Posted.Single(p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == SharedEventTypes.RealmJoinResolved);
            Assert.Equal("RealmJoinWatcher", info.Payload![SignalPayloadKeys.Source]);
            Assert.Equal("110", info.Payload["deploymentPhase"]);
        }

        [Fact]
        public void TriggerPhaseChangedFromTest_emits_informational_only_no_decision_signal()
        {
            using var f = new Fixture();
            using var adapter = new RealmJoinWatcherAdapter(f.Watcher, f.Ingress, f.Clock);

            adapter.TriggerPhaseChangedFromTest(prev: 100, curr: 200);

            // Phase change is observability-only. There is no DecisionSignalKind.RealmJoinPhaseChanged.
            Assert.DoesNotContain(f.Ingress.Posted, p => p.Kind == DecisionSignalKind.RealmJoinDetected);
            Assert.DoesNotContain(f.Ingress.Posted, p => p.Kind == DecisionSignalKind.RealmJoinResolved);

            var info = f.Ingress.Posted.Single(p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == SharedEventTypes.RealmJoinPhaseChanged);
            Assert.Equal("200", info.Payload!["deploymentPhase"]);
            Assert.Equal("100", info.Payload["previousPhase"]);
        }

        [Fact]
        public void TriggerPackageStartedFromTest_emits_signal_and_event_with_packageId_scope()
        {
            using var f = new Fixture();
            using var adapter = new RealmJoinWatcherAdapter(f.Watcher, f.Ingress, f.Clock);

            var snap = new RealmJoinPackageSnapshot(
                packageId: "generic-vlc",
                displayName: "VLC media player",
                version: "3.0.21.0",
                success: null,
                lastExitCode: null);
            adapter.TriggerPackageStartedFromTest(scope: RealmJoinPackageFact.ScopeMachine, snap: snap);

            var decision = f.Ingress.Posted.Single(p => p.Kind == DecisionSignalKind.RealmJoinPackageStarted);
            Assert.Equal("generic-vlc", decision.Payload![DecisionEngine.RealmJoinPayloadKeys.PackageId]);
            Assert.Equal("VLC media player", decision.Payload[DecisionEngine.RealmJoinPayloadKeys.DisplayName]);
            Assert.Equal("3.0.21.0", decision.Payload[DecisionEngine.RealmJoinPayloadKeys.Version]);
            Assert.Equal("machine", decision.Payload[DecisionEngine.RealmJoinPayloadKeys.Scope]);

            var info = f.Ingress.Posted.Single(p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == SharedEventTypes.RealmJoinPackageStarted);
            Assert.Equal("generic-vlc", info.Payload!["packageId"]);
            Assert.Equal("machine", info.Payload["scope"]);
        }

        [Fact]
        public void TriggerPackageCompletedFromTest_emits_signal_and_event_with_success_and_exit_code()
        {
            using var f = new Fixture();
            using var adapter = new RealmJoinWatcherAdapter(f.Watcher, f.Ingress, f.Clock);

            var snap = new RealmJoinPackageSnapshot(
                packageId: "generic-vlc",
                displayName: "VLC media player",
                version: "3.0.21.0",
                success: true,
                lastExitCode: 0);
            adapter.TriggerPackageCompletedFromTest(scope: RealmJoinPackageFact.ScopeMachine, snap: snap);

            var decision = f.Ingress.Posted.Single(p => p.Kind == DecisionSignalKind.RealmJoinPackageCompleted);
            Assert.Equal("true", decision.Payload![DecisionEngine.RealmJoinPayloadKeys.Success]);
            Assert.Equal("0", decision.Payload[DecisionEngine.RealmJoinPayloadKeys.LastExitCode]);

            var info = f.Ingress.Posted.Single(p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == SharedEventTypes.RealmJoinPackageCompleted);
            Assert.Equal("true", info.Payload!["success"]);
            Assert.Equal("0", info.Payload["lastExitCode"]);
        }

        [Fact]
        public void Overlong_display_name_is_truncated_to_256_characters()
        {
            using var f = new Fixture();
            using var adapter = new RealmJoinWatcherAdapter(f.Watcher, f.Ingress, f.Clock);

            var longName = new string('x', 1000);
            var snap = new RealmJoinPackageSnapshot(
                packageId: "generic-bigname",
                displayName: longName,
                version: null,
                success: null,
                lastExitCode: null);
            adapter.TriggerPackageStartedFromTest(scope: RealmJoinPackageFact.ScopeUser, snap: snap);

            var info = f.Ingress.Posted.Single(p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == SharedEventTypes.RealmJoinPackageStarted);
            // Adapter must clamp DisplayName before posting (PII / payload-size guard mirror of
            // RealmJoinPackageFact.MaxDisplayNameLength = 256). The packageId stays intact.
            Assert.Equal(256, info.Payload!["displayName"].Length);
            Assert.Equal("generic-bigname", info.Payload["packageId"]);
            Assert.Equal("user", info.Payload["scope"]);
        }
    }
}
