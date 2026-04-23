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

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring
{
    /// <summary>
    /// Plan §6 Fix 7 — the <see cref="EspAndHelloTracker"/> coordinator must NOT forward a
    /// Shell-Core <c>esp_exiting</c> as a synthetic <c>EspPhaseChanged(FinalizingSetup)</c>
    /// decision signal when the enrollment is a Classic V1 flow (SkipUser=false) and we have
    /// not yet seen any AccountSetup activity — because that first exit is the Device-ESP to
    /// Account-ESP handoff, not the true final ESP exit. Forwarding prematurely drives the
    /// reducer into <c>AwaitingHello</c> and arms HelloSafety from the wrong baseline (see
    /// session 30410cd7).
    /// <para>
    /// Exit forwards that must keep working:
    /// </para>
    /// <list type="bullet">
    ///   <item>SkipUser=true — device-only / full-skip flows; single esp_exiting IS final.</item>
    ///   <item>SkipUser unknown — defensive; never block when we don't know.</item>
    ///   <item>Non-<c>esp_exiting</c> reasons (e.g. <c>hello_wizard_started</c>) — the guard
    ///         only targets the ambiguous exit event.</item>
    ///   <item>SkipUser=false BUT AccountSetup activity observed — this is the post-AccountSetup
    ///         final exit.</item>
    /// </list>
    /// </summary>
    public sealed class EspAndHelloTrackerFinalizingGuardTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 4, 23, 18, 57, 45, DateTimeKind.Utc);

        private sealed class Fixture : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public AgentLogger Logger { get; }
            public FakeSignalIngressSink Ingress { get; } = new FakeSignalIngressSink();
            public VirtualClock Clock { get; } = new VirtualClock(Fixed);

            public EspAndHelloTracker BuildCoordinator(
                bool? skipUser,
                bool? skipDevice = false,
                bool accountSetupSeen = false)
            {
                var trackerPost = new InformationalEventPost(new FakeSignalIngressSink(), Clock);
                return new EspAndHelloTracker(
                    sessionId: "S1",
                    tenantId: "T1",
                    post: trackerPost,
                    logger: Logger,
                    skipConfigProbe: () => (skipUser, skipDevice),
                    accountSetupActivityProbe: () => accountSetupSeen);
            }

            public Fixture() { Logger = new AgentLogger(Tmp.Path, AgentLogLevel.Debug); }

            public void Dispose() { Tmp.Dispose(); }
        }

        [Fact]
        public void ForwardsFinalizing_whenSkipUserTrue_pluginsEspExit()
        {
            // SkipUser=true → single Shell-Core esp_exiting IS the final exit. Forward.
            using var f = new Fixture();
            using var coordinator = f.BuildCoordinator(skipUser: true);
            using var adapter = new EspAndHelloTrackerAdapter(coordinator, f.Ingress, f.Clock);

            coordinator.TriggerFinalizingSetupPhaseForTest("esp_exiting");

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.EspPhaseChanged, posted.Kind);
            Assert.Equal(EnrollmentPhase.FinalizingSetup.ToString(), posted.Payload![SignalPayloadKeys.EspPhase]);
            Assert.Equal("esp_exiting", posted.Payload["reason"]);
        }

        [Fact]
        public void DoesNotForwardFinalizing_whenSkipUserFalseAndNoAccountSetup()
        {
            // Classic V1 intermediate Device-ESP exit: SkipUser=false, AccountSetup not yet seen.
            // Swallow — the real final exit arrives later.
            using var f = new Fixture();
            using var coordinator = f.BuildCoordinator(skipUser: false, accountSetupSeen: false);
            using var adapter = new EspAndHelloTrackerAdapter(coordinator, f.Ingress, f.Clock);

            coordinator.TriggerFinalizingSetupPhaseForTest("esp_exiting");

            Assert.Empty(f.Ingress.Posted);
        }

        [Fact]
        public void ForwardsFinalizing_whenSkipUserFalseAndAccountSetupAlreadySeen()
        {
            // Post-AccountSetup second esp_exiting — genuine final exit. Forward.
            using var f = new Fixture();
            using var coordinator = f.BuildCoordinator(skipUser: false, accountSetupSeen: true);
            using var adapter = new EspAndHelloTrackerAdapter(coordinator, f.Ingress, f.Clock);

            coordinator.TriggerFinalizingSetupPhaseForTest("esp_exiting");

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.EspPhaseChanged, posted.Kind);
            Assert.Equal(EnrollmentPhase.FinalizingSetup.ToString(), posted.Payload![SignalPayloadKeys.EspPhase]);
        }

        [Fact]
        public void ForwardsFinalizing_whenSkipUserUnknown_regardlessOfAccountSetup()
        {
            // Defensive: unknown skip-flag → forward (preserves pre-fix behavior, no new breakage).
            using var f = new Fixture();
            using var coordinator = f.BuildCoordinator(skipUser: null, accountSetupSeen: false);
            using var adapter = new EspAndHelloTrackerAdapter(coordinator, f.Ingress, f.Clock);

            coordinator.TriggerFinalizingSetupPhaseForTest("esp_exiting");

            Assert.Single(f.Ingress.Posted);
        }

        [Fact]
        public void ForwardsFinalizing_whenHelloWizardStarted_regardlessOfSkipUser()
        {
            // Only esp_exiting is ambiguous. hello_wizard_started and other reasons must keep
            // forwarding unconditionally — SkipUser does not affect Hello-wizard detection.
            using var f = new Fixture();
            using var coordinator = f.BuildCoordinator(skipUser: false, accountSetupSeen: false);
            using var adapter = new EspAndHelloTrackerAdapter(coordinator, f.Ingress, f.Clock);

            coordinator.TriggerFinalizingSetupPhaseForTest("hello_wizard_started");

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.EspPhaseChanged, posted.Kind);
            Assert.Equal("hello_wizard_started", posted.Payload!["reason"]);
        }

        [Fact]
        public void SwallowedFirstExit_doesNotBlockSecondExitAfterAccountSetup()
        {
            // Full Classic V1 sequence: Device-ESP esp_exiting (swallowed) → AccountSetup
            // activity appears → second esp_exiting (forwarded). Mirrors the Fix-7 production
            // sequence for session 30410cd7.
            using var f = new Fixture();
            bool accountSetupSeen = false;
            var trackerPost = new InformationalEventPost(new FakeSignalIngressSink(), f.Clock);
            using var coordinator = new EspAndHelloTracker(
                sessionId: "S1",
                tenantId: "T1",
                post: trackerPost,
                logger: f.Logger,
                skipConfigProbe: () => ((bool?)false, (bool?)false),
                accountSetupActivityProbe: () => accountSetupSeen);
            using var adapter = new EspAndHelloTrackerAdapter(coordinator, f.Ingress, f.Clock);

            // First exit — swallowed.
            coordinator.TriggerFinalizingSetupPhaseForTest("esp_exiting");
            Assert.Empty(f.Ingress.Posted);

            // Now AccountSetup activity arrives (registry picks up the subcategory JSON).
            accountSetupSeen = true;

            // Second exit — forwarded.
            coordinator.TriggerFinalizingSetupPhaseForTest("esp_exiting");
            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.EspPhaseChanged, posted.Kind);
            Assert.Equal(EnrollmentPhase.FinalizingSetup.ToString(), posted.Payload![SignalPayloadKeys.EspPhase]);
        }
    }
}
