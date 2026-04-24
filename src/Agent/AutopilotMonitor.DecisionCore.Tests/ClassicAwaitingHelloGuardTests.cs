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

        // ======================================================= Guard matrix (shared contract)
        // The AwaitingHello guard applies the SAME unlock logic to both EspPhaseChanged
        // (FinalizingSetup) and EspExiting:
        //     unlock ⇔ (skipUser == true)  OR  (accountSetupEntered)
        // The two theories below exercise every cell of the 3×2 matrix (skipUser ∈
        // {true, false, null} × accountSetupEntered ∈ {false, true}) for each signal.
        // Rows marked NEW fill pre-existing coverage gaps (both-unlock combinations, and the
        // "null skipUser but AccountSetup-entered" unlock path that wasn't tested before).

        [Theory]
        [InlineData(true,  false, SessionStage.AwaitingHello)]    // skipUser unlocks
        [InlineData(true,  true,  SessionStage.AwaitingHello)]    // both unlocks present — NEW coverage
        [InlineData(false, false, SessionStage.EspDeviceSetup)]   // neither unlock → guard blocks
        [InlineData(false, true,  SessionStage.AwaitingHello)]    // accountSetup unlocks
        [InlineData(null,  false, SessionStage.EspDeviceSetup)]   // skipUser unknown → guard blocks
        [InlineData(null,  true,  SessionStage.AwaitingHello)]    // accountSetup overrides unknown skipUser — NEW coverage
        public void EspPhaseChanged_FinalizingSetup_guard_applies_skipUser_and_accountSetup_unlock_matrix(
            bool? skipUser, bool accountSetupEntered, SessionStage expectedStage)
        {
            var engine = new DecisionEngine();
            var initialStage = accountSetupEntered ? SessionStage.EspAccountSetup : SessionStage.EspDeviceSetup;
            var state = BuildState(stage: initialStage, skipUser: skipUser, accountSetupEntered: accountSetupEntered);

            var step = engine.Reduce(state, MakePhaseSignal(EnrollmentPhase.FinalizingSetup, ordinal: 5));

            Assert.Equal(expectedStage, step.NewState.Stage);
            Assert.True(step.Transition.Taken);
            // Observability is recorded regardless of the guard outcome.
            Assert.NotNull(step.NewState.FinalizingEnteredUtc);
            Assert.Equal(EnrollmentPhase.FinalizingSetup, step.NewState.CurrentEnrollmentPhase!.Value);
            // EspPhaseChanged never arms HelloSafety (only EspExiting does — see sibling theory).
            Assert.DoesNotContain(step.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety);
        }

        [Theory]
        [InlineData(true,  false, SessionStage.AwaitingHello,   true)]
        [InlineData(true,  true,  SessionStage.AwaitingHello,   true)]    // NEW coverage
        [InlineData(false, false, SessionStage.EspDeviceSetup,  false)]
        [InlineData(false, true,  SessionStage.AwaitingHello,   true)]
        [InlineData(null,  false, SessionStage.EspDeviceSetup,  false)]
        [InlineData(null,  true,  SessionStage.AwaitingHello,   true)]    // NEW coverage
        public void EspExiting_guard_controls_AwaitingHello_transition_and_arms_HelloSafety_only_on_unlock(
            bool? skipUser, bool accountSetupEntered, SessionStage expectedStage, bool expectHelloSafetyArm)
        {
            var engine = new DecisionEngine();
            var initialStage = accountSetupEntered ? SessionStage.EspAccountSetup : SessionStage.EspDeviceSetup;
            var state = BuildState(stage: initialStage, skipUser: skipUser, accountSetupEntered: accountSetupEntered);

            var step = engine.Reduce(state, MakeExitingSignal(ordinal: 5));

            Assert.Equal(expectedStage, step.NewState.Stage);
            Assert.True(step.Transition.Taken);
            // EspFinalExitUtc is always recorded for observability — even when the guard blocks.
            Assert.NotNull(step.NewState.EspFinalExitUtc);

            if (expectHelloSafetyArm)
            {
                Assert.Contains(step.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety);
                Assert.Contains(step.Effects,
                    e => e.Kind == DecisionEffectKind.ScheduleDeadline && e.Deadline?.Name == DeadlineNames.HelloSafety);
            }
            else
            {
                Assert.Empty(step.NewState.Deadlines);
                Assert.Empty(step.Effects);
            }
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
            Assert.True(afterConfig.ScenarioObservations.SkipUserEsp!.Value);
            Assert.Equal(EspConfig.DeviceEspOnly, afterConfig.ScenarioProfile.EspConfig);

            // Step 2 — EspPhaseChanged(FinalizingSetup) from EspAndHelloTrackerAdapter.
            var finalizing = MakePhaseSignal(EnrollmentPhase.FinalizingSetup, ordinal: 2);
            var afterFinalizing = engine.Reduce(afterConfig, finalizing);

            Assert.Equal(SessionStage.AwaitingHello, afterFinalizing.NewState.Stage);
        }

        [Fact]
        public void EspConfigDetectedSkipUserTrue_onlyHalfPayload_stillUnblocksAwaitingHello()
        {
            // Codex second-pass Hoch (post-#51) — partial-payload regression guard.
            // EspConfigDetected arrives with ONLY skipUser=true (skipDevice missing, e.g.
            // registry key not yet present). Under the original legacy code this was sufficient
            // to unlock AwaitingHello because the guard read SkipUserEsp?.Value directly. The
            // derived Profile.EspConfig enum would stay Unknown (it needs BOTH halves), so the
            // guard must NOT be gated on EspConfig. This test pins the behaviour: one half +
            // FinalizingSetup → AwaitingHello.
            var engine = new DecisionEngine();
            var seed = DecisionState.CreateInitial("s", "t")
                .ToBuilder()
                .WithStage(SessionStage.EspDeviceSetup)
                .WithStepIndex(1)
                .WithLastAppliedSignalOrdinal(0)
                .Build();

            var partial = new DecisionSignal(
                sessionSignalOrdinal: 1,
                sessionTraceOrdinal: 1,
                kind: DecisionSignalKind.EspConfigDetected,
                kindSchemaVersion: 1,
                occurredAtUtc: Fixed,
                sourceOrigin: "EnrollmentOrchestrator",
                evidence: new Evidence(EvidenceKind.Raw, "esp_config_detected_bootstrap_partial", "SkipUser=true only"),
                payload: new Dictionary<string, string>
                {
                    [SignalPayloadKeys.SkipUserEsp] = "true",
                    // NOTE: skipDevice key intentionally missing.
                });
            var afterConfig = engine.Reduce(seed, partial).NewState;

            // Only the raw half-fact is set; derived EspConfig enum stays Unknown.
            Assert.True(afterConfig.ScenarioObservations.SkipUserEsp!.Value);
            Assert.Null(afterConfig.ScenarioObservations.SkipDeviceEsp);
            Assert.Equal(EspConfig.Unknown, afterConfig.ScenarioProfile.EspConfig);

            // The guard MUST still unlock AwaitingHello — the semantic "Account-ESP is skipped"
            // is fully known from SkipUser alone, regardless of skipDevice.
            var finalizing = MakePhaseSignal(EnrollmentPhase.FinalizingSetup, ordinal: 2);
            var afterFinalizing = engine.Reduce(afterConfig, finalizing);
            Assert.Equal(SessionStage.AwaitingHello, afterFinalizing.NewState.Stage);
        }

        [Fact]
        public void EspExiting_partialSkipUserTrue_armsHelloSafety()
        {
            // Symmetric with EspExiting (the path that actually arms HelloSafety) — partial
            // bootstrap payload must not block this path either.
            var engine = new DecisionEngine();
            var seed = DecisionState.CreateInitial("s", "t")
                .ToBuilder()
                .WithStage(SessionStage.EspDeviceSetup)
                .WithStepIndex(1)
                .WithLastAppliedSignalOrdinal(0)
                .Build();

            var partial = new DecisionSignal(
                sessionSignalOrdinal: 1,
                sessionTraceOrdinal: 1,
                kind: DecisionSignalKind.EspConfigDetected,
                kindSchemaVersion: 1,
                occurredAtUtc: Fixed,
                sourceOrigin: "EnrollmentOrchestrator",
                evidence: new Evidence(EvidenceKind.Raw, "esp_config_detected_bootstrap_partial", "SkipUser=true only"),
                payload: new Dictionary<string, string>
                {
                    [SignalPayloadKeys.SkipUserEsp] = "true",
                });
            var afterConfig = engine.Reduce(seed, partial).NewState;
            var afterExit = engine.Reduce(afterConfig, MakeExitingSignal(ordinal: 2));

            Assert.Equal(SessionStage.AwaitingHello, afterExit.NewState.Stage);
            Assert.Contains(afterExit.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety);
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
            // Codex follow-up #5: the guard reads from Profile.EspConfig now. SkipUser=true
            // without a skipDevice observation would leave EspConfig=Unknown — so tests that
            // pre-seed "SkipUser=true" must also pre-seed skipDevice=false to land at
            // DeviceEspOnly (the semantic equivalent of the legacy "SkipUserEsp.Value == true").
            if (skipUser.HasValue)
            {
                builder.ScenarioObservations = builder.ScenarioObservations
                    .WithSkipUserEsp(skipUser.Value, sourceSignalOrdinal: 0)
                    .WithSkipDeviceEsp(value: false, sourceSignalOrdinal: 0);
                builder.ScenarioProfile = builder.ScenarioProfile.With(
                    espConfig: EnrollmentScenarioProfileUpdater.DeriveEspConfig(skipUser.Value, false),
                    confidence: ProfileConfidence.Medium,
                    evidenceOrdinal: 0);
            }
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
