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
using SharedEventTypes = AutopilotMonitor.Shared.Constants.EventTypes;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.SignalAdapters
{
    /// <summary>
    /// Plan §5 Fix 2 — the adapter must emit a <c>phase_transition</c> declaration event the
    /// first time app activity is observed in a given ESP phase, so the Web UI's PhaseTimeline
    /// opens Apps (Device) / Apps (User) sub-phase rows with a non-null duration.
    /// </summary>
    public sealed class ImeLogTrackerAdapterSubPhaseDeclarationTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 4, 23, 10, 0, 0, DateTimeKind.Utc);

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

            public IReadOnlyList<FakeSignalIngressSink.PostedSignal> InfoEvents(string eventType) =>
                Ingress.Posted
                    .Where(p => p.Kind == DecisionSignalKind.InformationalEvent
                                && p.Payload != null
                                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                                && et == eventType)
                    .ToList();

            public void Dispose()
            {
                Tracker.Dispose();
                Tmp.Dispose();
            }
        }

        [Fact]
        public void First_app_activity_in_DeviceSetup_emits_phase_transition_AppsDevice()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerEspPhaseFromTest("DeviceSetup");
            adapter.TriggerAppStateFromTest(new AppPackageState("app-1", 0),
                AppInstallationState.Unknown, AppInstallationState.Downloading);

            var phaseTransition = Assert.Single(f.InfoEvents(SharedEventTypes.PhaseTransition));
            Assert.Equal(nameof(EnrollmentPhase.AppsDevice), phaseTransition.Payload!["phase"]);
            Assert.Equal("true", phaseTransition.Payload[SignalPayloadKeys.ImmediateUpload]);
            Assert.Equal("DeviceSetup", phaseTransition.Payload["espPhase"]);
            Assert.Equal("first_app_activity_in_esp_phase", phaseTransition.Payload["trigger"]);
        }

        [Fact]
        public void First_app_activity_in_AccountSetup_emits_phase_transition_AppsUser()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerEspPhaseFromTest("DeviceSetup");
            adapter.TriggerAppStateFromTest(new AppPackageState("app-1", 0),
                AppInstallationState.Unknown, AppInstallationState.Installing);   // AppsDevice emitted

            adapter.TriggerEspPhaseFromTest("AccountSetup");
            adapter.TriggerAppStateFromTest(new AppPackageState("app-2", 1),
                AppInstallationState.Unknown, AppInstallationState.Installing);   // AppsUser emitted

            var transitions = f.InfoEvents(SharedEventTypes.PhaseTransition);
            Assert.Equal(2, transitions.Count);
            Assert.Equal(nameof(EnrollmentPhase.AppsDevice), transitions[0].Payload!["phase"]);
            Assert.Equal(nameof(EnrollmentPhase.AppsUser), transitions[1].Payload!["phase"]);
        }

        [Fact]
        public void SubPhase_declaration_fires_once_per_ESP_phase()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerEspPhaseFromTest("DeviceSetup");
            // Multiple app events in the same ESP phase → still only one phase_transition.
            adapter.TriggerAppStateFromTest(new AppPackageState("a", 0), AppInstallationState.Unknown, AppInstallationState.Downloading);
            adapter.TriggerAppStateFromTest(new AppPackageState("a", 0), AppInstallationState.Downloading, AppInstallationState.Installing);
            adapter.TriggerAppStateFromTest(new AppPackageState("a", 0), AppInstallationState.Installing, AppInstallationState.Installed);
            adapter.TriggerAppStateFromTest(new AppPackageState("b", 1), AppInstallationState.Unknown, AppInstallationState.Downloading);

            Assert.Single(f.InfoEvents(SharedEventTypes.PhaseTransition));
        }

        [Fact]
        public void App_activity_before_any_ESP_phase_does_not_emit_phase_transition()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            // WDP v2 / device-only paths have no ESP phase — adapter must not emit an
            // AppsDevice/AppsUser declaration without a DeviceSetup/AccountSetup anchor.
            adapter.TriggerAppStateFromTest(new AppPackageState("a", 0),
                AppInstallationState.Unknown, AppInstallationState.Installing);

            Assert.Empty(f.InfoEvents(SharedEventTypes.PhaseTransition));
        }

        [Fact]
        public void Unmapped_ESP_phase_does_not_emit_phase_transition()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            // "FinalizingSetup" maps to an EnrollmentPhase value, but the reducer emits the
            // phase_transition(FinalizingSetup) on its own from Fix 6 — the adapter must not
            // duplicate it on app activity.
            adapter.TriggerEspPhaseFromTest("FinalizingSetup");
            adapter.TriggerAppStateFromTest(new AppPackageState("a", 0),
                AppInstallationState.Unknown, AppInstallationState.Installing);

            Assert.Empty(f.InfoEvents(SharedEventTypes.PhaseTransition));
        }

        [Fact]
        public void Phase_transition_fires_before_the_app_state_event_on_the_wire()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerEspPhaseFromTest("DeviceSetup");
            adapter.TriggerAppStateFromTest(new AppPackageState("a", 0),
                AppInstallationState.Unknown, AppInstallationState.Installing);

            // Sequence in the ingress: first the esp_phase_changed InformationalEvent (from
            // EmitEspPhase), then the phase_transition, then the app_install_started. The phase
            // boundary must precede the first event inside it.
            var appEvents = f.InfoEvents(SharedEventTypes.AppInstallStart);
            var phaseEvents = f.InfoEvents(SharedEventTypes.PhaseTransition);

            Assert.Single(phaseEvents);
            Assert.Single(appEvents);

            var phaseIdx = f.Ingress.Posted.ToList().IndexOf(phaseEvents[0]);
            var appIdx = f.Ingress.Posted.ToList().IndexOf(appEvents[0]);
            Assert.True(phaseIdx < appIdx,
                $"phase_transition (idx={phaseIdx}) must be posted before app_install_started (idx={appIdx}).");
        }
    }
}
