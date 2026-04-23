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
    /// Plan §6 Fix 8 — the Classic reducer must only promote to <see cref="SessionStage.AwaitingHello"/>
    /// when the promotion is legitimate: either <see cref="DecisionState.AccountSetupEnteredUtc"/>
    /// is set (i.e. we're past the post-Account-ESP final exit), or
    /// <see cref="DecisionState.SkipUserEsp"/> is explicitly <c>true</c> (device-only / SkipUser
    /// flow where a single esp_exiting IS the final one).
    /// <para>
    /// Applies symmetrically to <see cref="DecisionSignalKind.EspPhaseChanged"/>(FinalizingSetup)
    /// AND <see cref="DecisionSignalKind.EspExiting"/>. On an early / intermediate signal the
    /// stage stays unchanged, no HelloSafety is armed, and observability facts
    /// (<see cref="DecisionState.FinalizingEnteredUtc"/> / <see cref="DecisionState.EspFinalExitUtc"/>)
    /// are still recorded so classifiers see the full picture.
    /// </para>
    /// </summary>
    public sealed class ClassicAwaitingHelloGuardTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 4, 23, 18, 57, 45, DateTimeKind.Utc);

        // ========================================================= EspPhaseChanged (Finalizing)

        [Fact]
        public void EspPhaseChanged_FinalizingSetup_withSkipUserTrue_transitionsToAwaitingHello()
        {
            var engine = new DecisionEngine();
            var state = BuildState(stage: SessionStage.EspDeviceSetup, skipUser: true);

            var step = engine.Reduce(state, MakePhaseSignal(EnrollmentPhase.FinalizingSetup, ordinal: 5));

            Assert.Equal(SessionStage.AwaitingHello, step.NewState.Stage);
            Assert.True(step.Transition.Taken);
            Assert.Equal(SessionStage.EspDeviceSetup, step.Transition.FromStage);
            Assert.Equal(SessionStage.AwaitingHello, step.Transition.ToStage);
            Assert.NotNull(step.NewState.FinalizingEnteredUtc);
            Assert.Equal(EnrollmentPhase.FinalizingSetup, step.NewState.CurrentEnrollmentPhase!.Value);
        }

        [Fact]
        public void EspPhaseChanged_FinalizingSetup_withSkipUserFalse_beforeAccountSetup_keepsStage()
        {
            // Classic intermediate Device-ESP exit synthesized as FinalizingSetup. Guard blocks
            // the AwaitingHello promotion; the stage stays on EspDeviceSetup.
            var engine = new DecisionEngine();
            var state = BuildState(stage: SessionStage.EspDeviceSetup, skipUser: false);

            var step = engine.Reduce(state, MakePhaseSignal(EnrollmentPhase.FinalizingSetup, ordinal: 5));

            Assert.Equal(SessionStage.EspDeviceSetup, step.NewState.Stage);
            Assert.True(step.Transition.Taken);
            Assert.Equal(step.Transition.FromStage, step.Transition.ToStage);
            // Observability is still recorded.
            Assert.NotNull(step.NewState.FinalizingEnteredUtc);
            Assert.Equal(EnrollmentPhase.FinalizingSetup, step.NewState.CurrentEnrollmentPhase!.Value);
            // No HelloSafety on this path (HandleEspPhaseChangedV1 never arms it — but verify
            // anyway for belt-and-suspenders).
            Assert.DoesNotContain(step.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety);
        }

        [Fact]
        public void EspPhaseChanged_FinalizingSetup_withSkipUserFalse_afterAccountSetup_transitionsToAwaitingHello()
        {
            // Post-Account-ESP final exit: guard allows promotion.
            var engine = new DecisionEngine();
            var state = BuildState(stage: SessionStage.EspAccountSetup, skipUser: false, accountSetupEntered: true);

            var step = engine.Reduce(state, MakePhaseSignal(EnrollmentPhase.FinalizingSetup, ordinal: 9));

            Assert.Equal(SessionStage.AwaitingHello, step.NewState.Stage);
            Assert.NotNull(step.NewState.FinalizingEnteredUtc);
        }

        [Fact]
        public void EspPhaseChanged_FinalizingSetup_withSkipUserUnknown_beforeAccountSetup_keepsStage()
        {
            // Defensive: unknown SkipUser does NOT unlock AwaitingHello. Otherwise a missing
            // EspConfigDetected signal (e.g. registry race at enrollment start) would reopen the
            // original bug.
            var engine = new DecisionEngine();
            var state = BuildState(stage: SessionStage.EspDeviceSetup, skipUser: null);

            var step = engine.Reduce(state, MakePhaseSignal(EnrollmentPhase.FinalizingSetup, ordinal: 3));

            Assert.Equal(SessionStage.EspDeviceSetup, step.NewState.Stage);
        }

        // =============================================================== EspExiting (real arm)

        [Fact]
        public void EspExiting_withSkipUserTrue_transitionsToAwaitingHello_armsHelloSafety()
        {
            var engine = new DecisionEngine();
            var state = BuildState(stage: SessionStage.EspDeviceSetup, skipUser: true);

            var step = engine.Reduce(state, MakeExitingSignal(ordinal: 5));

            Assert.Equal(SessionStage.AwaitingHello, step.NewState.Stage);
            Assert.NotNull(step.NewState.EspFinalExitUtc);
            Assert.Contains(step.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety);
            Assert.Contains(step.Effects,
                e => e.Kind == DecisionEffectKind.ScheduleDeadline && e.Deadline?.Name == DeadlineNames.HelloSafety);
        }

        [Fact]
        public void EspExiting_withSkipUserFalse_beforeAccountSetup_staysInStage_noHelloSafety_butRecordsFact()
        {
            // Device-ESP intermediate exit. Classic's HandleEspExitingV1 must preserve the
            // EspFinalExitUtc fact for observability but block the stage change + deadline arm.
            var engine = new DecisionEngine();
            var state = BuildState(stage: SessionStage.EspDeviceSetup, skipUser: false);

            var step = engine.Reduce(state, MakeExitingSignal(ordinal: 5));

            Assert.Equal(SessionStage.EspDeviceSetup, step.NewState.Stage);
            Assert.NotNull(step.NewState.EspFinalExitUtc);
            Assert.Empty(step.NewState.Deadlines);
            Assert.Empty(step.Effects);
            Assert.True(step.Transition.Taken);
        }

        [Fact]
        public void EspExiting_withSkipUserFalse_afterAccountSetup_transitionsToAwaitingHello_armsHelloSafety()
        {
            var engine = new DecisionEngine();
            var state = BuildState(
                stage: SessionStage.EspAccountSetup,
                skipUser: false,
                accountSetupEntered: true);

            var step = engine.Reduce(state, MakeExitingSignal(ordinal: 9));

            Assert.Equal(SessionStage.AwaitingHello, step.NewState.Stage);
            Assert.Contains(step.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety);
        }

        [Fact]
        public void EspExiting_withSkipUserUnknown_beforeAccountSetup_staysInStage()
        {
            var engine = new DecisionEngine();
            var state = BuildState(stage: SessionStage.EspDeviceSetup, skipUser: null);

            var step = engine.Reduce(state, MakeExitingSignal(ordinal: 5));

            Assert.Equal(SessionStage.EspDeviceSetup, step.NewState.Stage);
            Assert.Empty(step.NewState.Deadlines);
        }

        // ====================================================== Codex review — ordering contract

        [Fact]
        public void EspConfigDetectedSkipUserTrue_beforeFinalizing_unblocksAwaitingHello()
        {
            // Codex PR-1-pass-1 Hoch — documents the ordering contract that
            // EnrollmentOrchestrator.PostEspConfigDetectedBootstrap must preserve:
            // EspConfigDetected(skipUser=true) landed first → reducer's SkipUserEsp fact set →
            // subsequent EspPhaseChanged(FinalizingSetup) promotes to AwaitingHello as intended
            // on SkipUser=true flows. Regression guard: if the bootstrap ever regresses to
            // being posted AFTER collector start (e.g. from the background CollectAll path),
            // the adapter's _finalizingPosted fire-once flag would make the forward unrecoverable.
            var engine = new DecisionEngine();
            var seed = DecisionState.CreateInitial("s", "t")
                .ToBuilder()
                .WithStage(SessionStage.EspDeviceSetup)
                .WithStepIndex(1)
                .WithLastAppliedSignalOrdinal(0)
                .Build();

            // Step 1 — EspConfigDetected (bootstrap ordering).
            var espConfig = new DecisionSignal(
                sessionSignalOrdinal: 1,
                sessionTraceOrdinal: 1,
                kind: DecisionSignalKind.EspConfigDetected,
                kindSchemaVersion: 1,
                occurredAtUtc: Fixed,
                sourceOrigin: "EnrollmentOrchestrator",
                evidence: new Evidence(EvidenceKind.Raw, "esp_config_detected_bootstrap", "SkipUser=true, SkipDevice=false"),
                payload: new Dictionary<string, string>
                {
                    [SignalPayloadKeys.SkipUserEsp] = "true",
                    [SignalPayloadKeys.SkipDeviceEsp] = "false",
                });
            var afterConfig = engine.Reduce(seed, espConfig).NewState;
            Assert.True(afterConfig.SkipUserEsp!.Value);

            // Step 2 — EspPhaseChanged(FinalizingSetup) from EspAndHelloTrackerAdapter.
            var finalizing = MakePhaseSignal(EnrollmentPhase.FinalizingSetup, ordinal: 2);
            var afterFinalizing = engine.Reduce(afterConfig, finalizing);

            Assert.Equal(SessionStage.AwaitingHello, afterFinalizing.NewState.Stage);
        }

        [Fact]
        public void Finalizing_beforeEspConfigDetected_staysInStage_evenIfRegistryWouldSaySkipUser()
        {
            // Regression guard for the OPPOSITE ordering: this MUST keep blocking, because the
            // reducer has no way to know SkipUser is true until EspConfigDetected arrives. That
            // is precisely why the orchestrator bootstrap posts EspConfigDetected BEFORE any
            // collector can produce EspPhaseChanged. If this test ever flips to "AwaitingHello"
            // without the bootstrap signal, the reducer's guard has regressed.
            var engine = new DecisionEngine();
            var seed = DecisionState.CreateInitial("s", "t")
                .ToBuilder()
                .WithStage(SessionStage.EspDeviceSetup)
                .WithStepIndex(1)
                .WithLastAppliedSignalOrdinal(0)
                .Build();

            var finalizing = MakePhaseSignal(EnrollmentPhase.FinalizingSetup, ordinal: 1);
            var step = engine.Reduce(seed, finalizing);

            Assert.Equal(SessionStage.EspDeviceSetup, step.NewState.Stage);
        }

        // ====================================================================== test helpers

        private static DecisionState BuildState(
            SessionStage stage,
            bool? skipUser,
            bool accountSetupEntered = false)
        {
            var builder = DecisionState.CreateInitial("s", "t")
                .ToBuilder()
                .WithStage(stage)
                .WithStepIndex(2)
                .WithLastAppliedSignalOrdinal(1);
            if (skipUser.HasValue) builder.SkipUserEsp = new SignalFact<bool>(skipUser.Value, 0);
            if (accountSetupEntered) builder.AccountSetupEnteredUtc = new SignalFact<DateTime>(Fixed.AddMinutes(-1), 1);
            return builder.Build();
        }

        private static DecisionSignal MakePhaseSignal(EnrollmentPhase phase, long ordinal) =>
            new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: DecisionSignalKind.EspPhaseChanged,
                kindSchemaVersion: 1,
                occurredAtUtc: Fixed,
                sourceOrigin: "EspAndHelloTracker",
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: "esp-phase-detector-v1",
                    summary: phase.ToString(),
                    derivationInputs: new Dictionary<string, string>
                    {
                        [SignalPayloadKeys.EspPhase] = phase.ToString(),
                    }),
                payload: new Dictionary<string, string>
                {
                    [SignalPayloadKeys.EspPhase] = phase.ToString(),
                });

        private static DecisionSignal MakeExitingSignal(long ordinal) =>
            new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: DecisionSignalKind.EspExiting,
                kindSchemaVersion: 1,
                occurredAtUtc: Fixed,
                sourceOrigin: "ShellCoreTracker",
                evidence: new Evidence(EvidenceKind.Raw, "esp_exiting", "ESP phase exiting"));
    }
}
