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
    /// Plan §6 Fix 8 + session 330f73f3 fix (2026-05-18). The Classic reducer must only promote
    /// to <see cref="SessionStage.AwaitingHello"/> when the promotion is legitimate:
    /// <list type="bullet">
    ///   <item><see cref="DecisionState.AccountSetupProvisioningSucceededUtc"/> is set
    ///         (AccountSetupCategory registry resolved as succeeded, OR the fallback fired),
    ///         i.e. ESP is genuinely done, OR</item>
    ///   <item><see cref="EnrollmentScenarioObservations.SkipUserEsp"/> is observed as
    ///         <c>true</c> (device-only / SkipUser flow where a single esp_exiting IS the
    ///         final one).</item>
    /// </list>
    /// The pre-fix gate "<c>AccountSetupEnteredUtc != null</c>" was too weak — Shell-Core
    /// event 62407 fires at every ESP-page transition, and the Device→Account handoff would
    /// promote and arm HelloSafety just seconds after AccountSetup entered, while apps were
    /// still installing. See session 330f73f3 for the production failure mode.
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
        // Unlock condition (post-330f73f3):
        //     unlock ⇔ (skipUser == true)  OR  (accountSetupSucceeded)
        // AccountSetup *entered* alone is NOT sufficient (the pre-fix gate that caused 330f73f3).
        // The two theories below sweep skipUser ∈ {true, false, null} × accountSetupSucceeded ∈
        // {false, true}, and additionally cover an "entered-but-not-succeeded" row to pin the
        // regression-blocking semantic.

        [Theory]
        // skipUser=true unlocks regardless of AccountSetup status
        [InlineData(true,  false, false, SessionStage.AwaitingHello)]
        [InlineData(true,  true,  true,  SessionStage.AwaitingHello)]
        // skipUser=false AND succeeded=true → unlock
        [InlineData(false, true,  true,  SessionStage.AwaitingHello)]
        // skipUser=false AND only entered → blocked (the 330f73f3 fix)
        [InlineData(false, true,  false, SessionStage.EspAccountSetup)]
        // neither unlock present → blocked
        [InlineData(false, false, false, SessionStage.EspDeviceSetup)]
        [InlineData(null,  false, false, SessionStage.EspDeviceSetup)]
        // skipUser unknown + only entered → blocked
        [InlineData(null,  true,  false, SessionStage.EspAccountSetup)]
        // skipUser unknown + succeeded → unlock
        [InlineData(null,  true,  true,  SessionStage.AwaitingHello)]
        public void EspPhaseChanged_FinalizingSetup_guard_applies_skipUser_and_accountSetup_unlock_matrix(
            bool? skipUser, bool accountSetupEntered, bool accountSetupSucceeded, SessionStage expectedStage)
        {
            var engine = new DecisionEngine();
            var initialStage = accountSetupEntered ? SessionStage.EspAccountSetup : SessionStage.EspDeviceSetup;
            var state = BuildState(
                stage: initialStage,
                skipUser: skipUser,
                accountSetupEntered: accountSetupEntered,
                accountSetupSucceeded: accountSetupSucceeded);

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
        // skipUser=true unlocks regardless of AccountSetup
        [InlineData(true,  false, false, SessionStage.AwaitingHello,   true)]
        [InlineData(true,  true,  true,  SessionStage.AwaitingHello,   true)]
        // skipUser=false + succeeded → unlock + arm
        [InlineData(false, true,  true,  SessionStage.AwaitingHello,   true)]
        // skipUser=false + only entered → blocked (the 330f73f3 fix); EspFinalExitUtc still set
        [InlineData(false, true,  false, SessionStage.EspAccountSetup, false)]
        // neither unlock → blocked
        [InlineData(false, false, false, SessionStage.EspDeviceSetup,  false)]
        [InlineData(null,  false, false, SessionStage.EspDeviceSetup,  false)]
        // skipUser unknown + only entered → blocked
        [InlineData(null,  true,  false, SessionStage.EspAccountSetup, false)]
        // skipUser unknown + succeeded → unlock
        [InlineData(null,  true,  true,  SessionStage.AwaitingHello,   true)]
        public void EspExiting_guard_controls_AwaitingHello_transition_and_arms_HelloSafety_only_on_unlock(
            bool? skipUser, bool accountSetupEntered, bool accountSetupSucceeded,
            SessionStage expectedStage, bool expectHelloSafetyArm)
        {
            var engine = new DecisionEngine();
            var initialStage = accountSetupEntered ? SessionStage.EspAccountSetup : SessionStage.EspDeviceSetup;
            var state = BuildState(
                stage: initialStage,
                skipUser: skipUser,
                accountSetupEntered: accountSetupEntered,
                accountSetupSucceeded: accountSetupSucceeded);

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
            else if (accountSetupEntered)
            {
                // Session 1ec8f4c6 (2026-06-12): a guard-blocked exit AFTER AccountSetup entry
                // no longer parks the session silently — it arms the shared completion-
                // resolution window (AdvisoryCompletion) instead. HelloSafety stays off.
                Assert.DoesNotContain(step.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety);
                Assert.Contains(step.NewState.Deadlines, d => d.Name == DeadlineNames.AdvisoryCompletion);
                Assert.Contains(step.Effects,
                    e => e.Kind == DecisionEffectKind.ScheduleDeadline && e.Deadline?.Name == DeadlineNames.AdvisoryCompletion);
            }
            else
            {
                // Pre-AccountSetup blocked exit (Device-ESP handoff): nothing is armed.
                Assert.Empty(step.NewState.Deadlines);
                Assert.Empty(step.Effects);
            }
        }

        // ====================================================== Session 330f73f3 reproduction
        // The bug session in concrete terms: AccountSetup entered at 07:04:47, intermediate
        // esp_exiting (Device→Account 62407) at 07:04:56 — 9 s later. The pre-fix reducer
        // promoted to AwaitingHello + armed HelloSafety against an in-progress AccountSetup;
        // 5 min later the HelloSafety synthetic-timeout fired enrollment_complete while apps
        // were still installing.

        [Fact]
        public void Session_330f73f3_intermediate_EspExiting_after_AccountSetupEntered_does_not_promote()
        {
            var engine = new DecisionEngine();
            var state = BuildState(
                stage: SessionStage.EspAccountSetup,
                skipUser: false,
                accountSetupEntered: true,
                accountSetupSucceeded: false);

            var step = engine.Reduce(state, MakeExitingSignal(ordinal: 5));

            // Stage unchanged — the intermediate 62407 is not a promotion trigger anymore.
            Assert.Equal(SessionStage.EspAccountSetup, step.NewState.Stage);
            // HelloSafety NOT armed — that was the load-bearing bit of the bug.
            Assert.DoesNotContain(step.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety);
            // Observability fact still recorded so downstream classifiers + Inspector see it.
            Assert.NotNull(step.NewState.EspFinalExitUtc);
        }

        [Fact]
        public void Session_330f73f3_AccountSetupProvisioningComplete_after_intermediate_EspExiting_does_deferred_promote()
        {
            // Continuation of the bug fix: the first esp_exiting was ignored (intermediate),
            // and Windows never fires a second one in this scenario (verified in 330f73f3 events).
            // When the strong fact finally arrives, the handler performs the deferred promotion
            // and arms HelloSafety against the AccountSetup-succeeded instant — not the
            // historical EspFinalExitUtc — so the 5-min window is meaningful.
            var engine = new DecisionEngine();
            var state = BuildState(
                stage: SessionStage.EspAccountSetup,
                skipUser: false,
                accountSetupEntered: true,
                accountSetupSucceeded: false);

            // Step 1: intermediate EspExiting → ignored.
            var afterExiting = engine.Reduce(state, MakeExitingSignal(ordinal: 5)).NewState;
            Assert.Equal(SessionStage.EspAccountSetup, afterExiting.Stage);
            Assert.NotNull(afterExiting.EspFinalExitUtc);

            // Step 2: AccountSetupProvisioningComplete arrives → deferred promote + HelloSafety arm.
            var completeSignal = MakeAccountSetupCompleteSignal(ordinal: 6, occurredAtUtc: Fixed.AddMinutes(5));
            var afterComplete = engine.Reduce(afterExiting, completeSignal);

            Assert.Equal(SessionStage.AwaitingHello, afterComplete.NewState.Stage);
            Assert.NotNull(afterComplete.NewState.AccountSetupProvisioningSucceededUtc);
            var helloSafety = Assert.Single(
                afterComplete.NewState.Deadlines,
                d => d.Name == DeadlineNames.HelloSafety);
            // Window is 300 s from the fact instant (not the historical EspExiting at ordinal 5).
            Assert.Equal(Fixed.AddMinutes(5).AddSeconds(300), helloSafety.DueAtUtc);
            Assert.Contains(afterComplete.Effects,
                e => e.Kind == DecisionEffectKind.ScheduleDeadline && e.Deadline?.Name == DeadlineNames.HelloSafety);
        }

        [Fact]
        public void AccountSetupProvisioningComplete_after_guard_blocked_FinalizingSetup_does_deferred_promote()
        {
            // Sibling-ordering: ShellCoreTracker's FinalizingSetupPhaseTriggered (62404 / first
            // 62407) is forwarded as EspPhaseChanged(FinalizingSetup) — the adapter fire-once-
            // dedupes this. If it arrives BEFORE AccountSetupProvisioningComplete, the new strong
            // gate blocks the stage transition but FinalizingEnteredUtc is still recorded. With
            // no second Finalizing forward coming, the deferred-promotion path in
            // HandleAccountSetupProvisioningCompleteV1 must also unlock from FinalizingEnteredUtc,
            // not only EspFinalExitUtc.
            var engine = new DecisionEngine();
            var state = BuildState(
                stage: SessionStage.EspAccountSetup,
                skipUser: false,
                accountSetupEntered: true,
                accountSetupSucceeded: false);

            // Step 1: guard-blocked Finalizing.
            var afterFinalizing = engine.Reduce(state,
                MakePhaseSignal(EnrollmentPhase.FinalizingSetup, ordinal: 5)).NewState;
            Assert.Equal(SessionStage.EspAccountSetup, afterFinalizing.Stage);
            Assert.NotNull(afterFinalizing.FinalizingEnteredUtc);
            Assert.Null(afterFinalizing.EspFinalExitUtc);
            Assert.DoesNotContain(afterFinalizing.Deadlines, d => d.Name == DeadlineNames.HelloSafety);

            // Step 2: AccountSetupProvisioningComplete → deferred promote + HelloSafety arm
            // anchored at the AccountSetup-succeeded instant.
            var afterComplete = engine.Reduce(afterFinalizing,
                MakeAccountSetupCompleteSignal(ordinal: 6, occurredAtUtc: Fixed.AddMinutes(5)));
            Assert.Equal(SessionStage.AwaitingHello, afterComplete.NewState.Stage);
            var helloSafety = Assert.Single(
                afterComplete.NewState.Deadlines,
                d => d.Name == DeadlineNames.HelloSafety);
            Assert.Equal(Fixed.AddMinutes(5).AddSeconds(300), helloSafety.DueAtUtc);
            Assert.Contains(afterComplete.Effects,
                e => e.Kind == DecisionEffectKind.ScheduleDeadline && e.Deadline?.Name == DeadlineNames.HelloSafety);
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
            bool accountSetupEntered = false,
            bool accountSetupSucceeded = false)
        {
            // Pin AgentBootUtc to a time before Fixed so EffectiveDeadlineBase doesn't floor
            // the fixture's deterministic timestamps to wall-clock-now.
            var builder = DecisionState.CreateInitial("s", "t", Fixed.AddDays(-1))
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
            // Session 330f73f3 fix: pre-seed the strong post-AccountSetup gate when the test
            // wants to exercise the "ESP genuinely done" path.
            if (accountSetupSucceeded)
                builder.AccountSetupProvisioningSucceededUtc = new SignalFact<DateTime>(Fixed.AddSeconds(-30), 1);
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

        private static DecisionSignal MakeAccountSetupCompleteSignal(long ordinal, DateTime occurredAtUtc) =>
            new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: DecisionSignalKind.AccountSetupProvisioningComplete,
                kindSchemaVersion: 1,
                occurredAtUtc: occurredAtUtc,
                sourceOrigin: "ProvisioningStatusTracker",
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: "provisioning-status-tracker-v1",
                    summary: "AccountSetup succeeded",
                    derivationInputs: new Dictionary<string, string>
                    {
                        ["accountSetupResolved"] = "true",
                    }),
                payload: new Dictionary<string, string>
                {
                    ["accountSetupResolved"] = "true",
                });
    }
}
