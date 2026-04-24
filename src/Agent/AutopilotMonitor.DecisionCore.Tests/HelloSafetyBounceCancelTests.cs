using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests
{
    /// <summary>
    /// Plan §6 Fix 10 — defensive: if a premature <c>EspPhaseChanged(FinalizingSetup)</c>
    /// moved the stage to <see cref="SessionStage.AwaitingHello"/> before AccountSetup had
    /// been observed, and HelloSafety was armed from that wrong baseline, a subsequent
    /// <c>EspPhaseChanged(AccountSetup)</c> must cancel the stale deadline so it cannot fire
    /// from the wrong window.
    /// <para>
    /// Mirrors the pathology observed in session <c>30410cd7</c>: HelloSafety armed at
    /// 18:57:45 (Device-ESP exit synthesized as Finalizing) and was never cancelled when the
    /// stage bounced back to EspAccountSetup at 18:57:55 — recovery only came from Hello
    /// resolving at 19:03:38, 7m13s after arm vs. a 5-min window. Under slower conditions the
    /// deadline would have fired first and mis-terminated the session.
    /// </para>
    /// </summary>
    public sealed class HelloSafetyBounceCancelTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 4, 23, 18, 57, 55, DateTimeKind.Utc);

        [Fact]
        public void EspPhaseChanged_AccountSetup_whileInAwaitingHello_cancelsHelloSafetyDeadline()
        {
            var engine = new DecisionEngine();

            // Starting state: stage AwaitingHello with HelloSafety armed (simulates the pre-fix
            // premature transition from Device-ESP Finalizing synthesis at 18:57:45).
            var helloSafetyDeadline = new ActiveDeadline(
                name: DeadlineNames.HelloSafety,
                dueAtUtc: Fixed.AddSeconds(-10).AddMinutes(5), // 5 min after the early arm
                firesSignalKind: DecisionSignalKind.DeadlineFired,
                firesPayload: new Dictionary<string, string>
                {
                    [SignalPayloadKeys.Deadline] = DeadlineNames.HelloSafety,
                });
            var seed = DecisionState.CreateInitial("s", "t")
                .ToBuilder()
                .WithStage(SessionStage.AwaitingHello)
                .WithStepIndex(3)
                .WithLastAppliedSignalOrdinal(2)
                .AddDeadline(helloSafetyDeadline)
                .Build();

            // Legitimate AccountSetup signal arrives (the bounce-back).
            var signal = new DecisionSignal(
                sessionSignalOrdinal: 3,
                sessionTraceOrdinal: 3,
                kind: DecisionSignalKind.EspPhaseChanged,
                kindSchemaVersion: 1,
                occurredAtUtc: Fixed,
                sourceOrigin: "ProvisioningStatusTracker",
                evidence: new Evidence(
                    kind: EvidenceKind.Raw,
                    identifier: "esp_phase_changed",
                    summary: "AccountSetup phase reached"),
                payload: new Dictionary<string, string>
                {
                    [SignalPayloadKeys.EspPhase] = EnrollmentPhase.AccountSetup.ToString(),
                });

            var step = engine.Reduce(seed, signal);

            // Stage bounces back to EspAccountSetup.
            Assert.Equal(SessionStage.EspAccountSetup, step.NewState.Stage);

            // The stale HelloSafety deadline must be gone.
            Assert.DoesNotContain(step.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety);

            // And a CancelDeadline effect for HelloSafety must have been emitted so the
            // orchestrator's DeadlineScheduler unschedules it.
            Assert.Contains(step.Effects, e =>
                e.Kind == DecisionEffectKind.CancelDeadline &&
                e.CancelDeadlineName == DeadlineNames.HelloSafety);

            // Existing DeviceOnlyEspDetection-cancel behavior must continue to fire too.
            Assert.Contains(step.Effects, e =>
                e.Kind == DecisionEffectKind.CancelDeadline &&
                e.CancelDeadlineName == DeadlineNames.DeviceOnlyEspDetection);
        }

        [Fact]
        public void EspPhaseChanged_AccountSetup_fromEspDeviceSetup_doesNotEmitHelloSafetyCancelWhenNoneArmed()
        {
            // Sanity: Fix 10 must NOT synthesize a spurious HelloSafety cancel when the state
            // was never in AwaitingHello and no HelloSafety deadline is armed. Backward compat
            // with all existing Classic fixtures.
            var engine = new DecisionEngine();
            var seed = DecisionState.CreateInitial("s", "t")
                .ToBuilder()
                .WithStage(SessionStage.EspDeviceSetup)
                .WithStepIndex(1)
                .WithLastAppliedSignalOrdinal(0)
                .Build();

            var signal = new DecisionSignal(
                sessionSignalOrdinal: 1,
                sessionTraceOrdinal: 1,
                kind: DecisionSignalKind.EspPhaseChanged,
                kindSchemaVersion: 1,
                occurredAtUtc: Fixed,
                sourceOrigin: "ProvisioningStatusTracker",
                evidence: new Evidence(EvidenceKind.Raw, "esp_phase_changed", "AccountSetup"),
                payload: new Dictionary<string, string>
                {
                    [SignalPayloadKeys.EspPhase] = EnrollmentPhase.AccountSetup.ToString(),
                });

            var step = engine.Reduce(seed, signal);

            Assert.Equal(SessionStage.EspAccountSetup, step.NewState.Stage);
            // Only DeviceOnlyEspDetection cancel effect — no HelloSafety effect (precondition
            // not met).
            Assert.DoesNotContain(step.Effects, e =>
                e.Kind == DecisionEffectKind.CancelDeadline &&
                e.CancelDeadlineName == DeadlineNames.HelloSafety);
            Assert.Contains(step.Effects, e =>
                e.Kind == DecisionEffectKind.CancelDeadline &&
                e.CancelDeadlineName == DeadlineNames.DeviceOnlyEspDetection);
        }
    }
}
