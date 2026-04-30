using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.DecisionCore.Classifiers;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests
{
    /// <summary>
    /// Option 3 (WG Part 1 graceful-exit hardening, 2026-04-30) — covers the slow-path
    /// fallback in <see cref="DecisionEngine"/>.HandleWhiteGloveShellCoreSuccessV1.
    /// <para>
    /// The fast-path is exercised by the scenario-replay tests in
    /// <c>WhiteGlovePart1ScenarioTests</c>. Here we verify that when an excluding
    /// observation already lives on state (AccountSetup activity), the inline classifier
    /// scores below <see cref="WhiteGloveSealingClassifier.HighThreshold"/> and the
    /// engine emits the legacy <see cref="DecisionEffectKind.RunClassifier"/> +
    /// <see cref="DecisionEffectKind.ScheduleDeadline"/>(<see cref="DeadlineNames.ClassifierTick"/>)
    /// effects instead of transitioning to <see cref="SessionStage.WhiteGloveSealed"/>.
    /// </para>
    /// </summary>
    public sealed class WhiteGloveFastPathFallbackTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 4, 30, 8, 45, 0, DateTimeKind.Utc);

        [Fact]
        public void ShellCoreSuccess_with_account_setup_activity_falls_back_to_slow_path()
        {
            var engine = new DecisionEngine();

            // Pre-seed AccountSetupEnteredUtc — once the AccountSetup ESP phase fires the
            // WG sealing classifier's HasAccountSetupActivity excluder kicks in (-40),
            // pushing ShellCore-alone (+80) to score 40 (Weak), below HighThreshold (70).
            var state = DecisionState.CreateInitial("s-fb", "t-fb").ToBuilder()
                .WithStage(SessionStage.EspAccountSetup)
                .WithStepIndex(3)
                .WithLastAppliedSignalOrdinal(2);
            state.AccountSetupEnteredUtc = new SignalFact<DateTime>(T0.AddMinutes(-2), 2);

            var shellCore = MakeShellCoreSignal(ordinal: 3);
            var step = engine.Reduce(state.Build(), shellCore);

            // Slow path — stage unchanged, no WhiteGloveSealed transition.
            Assert.NotEqual(SessionStage.WhiteGloveSealed, step.NewState.Stage);
            Assert.Equal(SessionStage.EspAccountSetup, step.NewState.Stage);

            // The observation IS still recorded on the new state — slow path is about
            // *how* we get to a verdict, not whether we record the signal.
            Assert.NotNull(step.NewState.ScenarioObservations.ShellCoreWhiteGloveSuccessSeen);
            Assert.True(step.NewState.ScenarioObservations.ShellCoreWhiteGloveSuccessSeen!.Value);

            // Slow path emits the RunClassifier effect (the EffectRunner will execute the
            // classifier, post ClassifierVerdictIssued, and the verdict-applier handles
            // the asymmetric-conservative rejection at HypothesisLevel.Weak).
            Assert.Contains(step.Effects, e => e.Kind == DecisionEffectKind.RunClassifier);

            // And arms the 30s ClassifierTick deadline so a later state change (e.g. an
            // AAD-sign-in invalidating the WG hypothesis) gets re-evaluated.
            Assert.Contains(step.Effects, e =>
                e.Kind == DecisionEffectKind.ScheduleDeadline &&
                e.Deadline?.Name == DeadlineNames.ClassifierTick);

            // Crucially, the fast-path's WhiteGloveSealing outcome is NOT recorded — that
            // path only writes a verdict on the inline-Confirmed branch.
            Assert.Equal(HypothesisLevel.Unknown,
                step.NewState.ClassifierOutcomes.WhiteGloveSealing.Level);
        }

        [Fact]
        public void ShellCoreSuccess_in_clean_state_takes_fast_path_in_single_step()
        {
            var engine = new DecisionEngine();

            // Clean state — no excluders. ShellCoreSuccess alone should hit Confirmed
            // inline and seal in this single Reduce call.
            var state = DecisionState.CreateInitial("s-fp", "t-fp").ToBuilder()
                .WithStage(SessionStage.EspDeviceSetup)
                .WithStepIndex(1)
                .WithLastAppliedSignalOrdinal(0)
                .Build();

            var shellCore = MakeShellCoreSignal(ordinal: 1);
            var step = engine.Reduce(state, shellCore);

            // Fast path — terminal stage in one step.
            Assert.Equal(SessionStage.WhiteGloveSealed, step.NewState.Stage);
            Assert.Equal(SessionOutcome.WhiteGlovePart1Sealed, step.NewState.Outcome);
            Assert.Equal(HypothesisLevel.Confirmed,
                step.NewState.ClassifierOutcomes.WhiteGloveSealing.Level);

            // No RunClassifier / ClassifierTick effects — the engine classified inline.
            Assert.DoesNotContain(step.Effects, e => e.Kind == DecisionEffectKind.RunClassifier);
            Assert.DoesNotContain(step.Effects, e =>
                e.Kind == DecisionEffectKind.ScheduleDeadline &&
                e.Deadline?.Name == DeadlineNames.ClassifierTick);

            // Single timeline-emit effect: the whiteglove_complete event.
            var emit = Assert.Single(step.Effects, e => e.Kind == DecisionEffectKind.EmitEventTimelineEntry);
            Assert.NotNull(emit.Parameters);
            Assert.Equal("whiteglove_complete", emit.Parameters!["eventType"]);

            // No deadlines left armed — terminal stage clears them.
            Assert.Empty(step.NewState.Deadlines);
        }

        private static DecisionSignal MakeShellCoreSignal(long ordinal) =>
            new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: DecisionSignalKind.WhiteGloveShellCoreSuccess,
                kindSchemaVersion: 1,
                occurredAtUtc: T0,
                sourceOrigin: "ShellCoreTracker",
                evidence: new Evidence(
                    kind: EvidenceKind.Raw,
                    identifier: "ShellCore-62407",
                    summary: "WhiteGlove_Success"),
                payload: new Dictionary<string, string>());
    }
}
