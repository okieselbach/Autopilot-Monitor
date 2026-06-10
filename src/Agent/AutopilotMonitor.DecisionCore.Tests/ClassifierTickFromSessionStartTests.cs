using System;
using System.Linq;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests
{
    /// <summary>
    /// Wave 2 / H2 — the <c>ClassifierTick</c> is armed REACTIVELY on the first primary WhiteGlove
    /// signal (shellcore_wg_success / sealing_pattern), NOT eagerly at <c>SessionStarted</c>.
    /// <para>
    /// Safety argument: the tick drives only <see cref="WhiteGloveSealingClassifier"/>, which can
    /// reach <see cref="HypothesisLevel.Confirmed"/> (the sole level that seals, threshold 70) only
    /// with a primary signal — the secondary-only ceiling is device_only(15)+system_reboot(15)=30.
    /// Both primaries arm the tick via <c>AttachWhiteGloveClassifierEffects</c> and it re-arms until
    /// terminal, so late secondary facts are still caught. Eager arming at SessionStarted only
    /// produced ~720 no-op ticks/session on the ~95% of enrollments that never see a WG signal.
    /// </para>
    /// </summary>
    public sealed class ClassifierTickFromSessionStartTests
    {
        private static readonly DateTime SessionStartUtc = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        private static DecisionState FreshState() =>
            DecisionState.CreateInitial("s", "t", SessionStartUtc);

        [Fact]
        public void SessionStarted_does_NOT_arm_ClassifierTick()
        {
            // H2 core: SessionStarted is a pure lifecycle anchor — no periodic classifier tick.
            var engine = new DecisionEngine();
            var step = engine.Reduce(FreshState(), MakeSessionStarted(ordinal: 0));

            Assert.True(step.Transition.Taken);
            Assert.Equal(SessionStage.SessionStarted, step.NewState.Stage);
            Assert.Empty(step.NewState.Deadlines);
            Assert.Empty(step.Effects);
        }

        [Fact]
        public void First_primary_WhiteGlove_signal_arms_ClassifierTick_reactively()
        {
            var engine = new DecisionEngine();
            var state = engine.Reduce(FreshState(), MakeSessionStarted(ordinal: 0)).NewState;
            Assert.Empty(state.Deadlines); // precondition: nothing armed yet

            var wgAt = SessionStartUtc.AddMinutes(5);
            var step = engine.Reduce(state, MakeSealingPatternDetected(ordinal: 1, occurredAtUtc: wgAt));

            // Deadline now armed, dueAt deterministic from the WG signal time (+30s).
            var tick = Assert.Single(step.NewState.Deadlines);
            Assert.Equal(DeadlineNames.ClassifierTick, tick.Name);
            Assert.Equal(wgAt.AddSeconds(30), tick.DueAtUtc);

            // The effects carry a ScheduleDeadline for the tick (plus the RunClassifier effect).
            Assert.Contains(step.Effects, e =>
                e.Kind == DecisionEffectKind.ScheduleDeadline
                && e.Deadline != null
                && e.Deadline.Name == DeadlineNames.ClassifierTick);
        }

        [Fact]
        public void DeadEnd_SessionStarted_on_active_state_does_NOT_arm_ClassifierTick()
        {
            // Dead-end branch (replay of truncated log, session already mid-flight) must not
            // reset state; specifically, no new deadline / effect.
            var engine = new DecisionEngine();
            var active = FreshState()
                .ToBuilder()
                .WithStage(SessionStage.AwaitingHello)
                .WithStepIndex(5)
                .WithLastAppliedSignalOrdinal(4)
                .Build();

            var step = engine.Reduce(active, MakeSessionStarted(ordinal: 5));

            Assert.False(step.Transition.Taken);
            Assert.Empty(step.NewState.Deadlines);
            Assert.Empty(step.Effects);
        }

        [Fact]
        public void Reactively_armed_ClassifierTick_fires_and_rearms()
        {
            // End-to-end: a WG signal arms the tick; firing the deadline runs the classifier +
            // re-arms (so late secondary facts are still picked up on genuine WG sessions).
            var engine = new DecisionEngine();
            var state = engine.Reduce(FreshState(), MakeSessionStarted(ordinal: 0)).NewState;

            var wgAt = SessionStartUtc.AddMinutes(5);
            state = engine.Reduce(state, MakeSealingPatternDetected(ordinal: 1, occurredAtUtc: wgAt)).NewState;
            Assert.Single(state.Deadlines);

            var firedStep = engine.Reduce(state, MakeDeadlineFired(
                ordinal: 2,
                occurredAtUtc: wgAt.AddSeconds(30),
                deadlineName: DeadlineNames.ClassifierTick));

            Assert.True(firedStep.Transition.Taken);
            var tick = Assert.Single(firedStep.NewState.Deadlines);
            Assert.Equal(DeadlineNames.ClassifierTick, tick.Name);
            Assert.Equal(wgAt.AddSeconds(60), tick.DueAtUtc); // re-armed +30s from the fire time
        }

        private static DecisionSignal MakeSessionStarted(long ordinal) =>
            new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: DecisionSignalKind.SessionStarted,
                kindSchemaVersion: 1,
                occurredAtUtc: SessionStartUtc,
                sourceOrigin: "test",
                evidence: new Evidence(EvidenceKind.Synthetic, "session:started", "test"));

        private static DecisionSignal MakeSealingPatternDetected(long ordinal, DateTime occurredAtUtc) =>
            new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: DecisionSignalKind.WhiteGloveSealingPatternDetected,
                kindSchemaVersion: 1,
                occurredAtUtc: occurredAtUtc,
                sourceOrigin: "test",
                evidence: new Evidence(EvidenceKind.Synthetic, "wg:sealing-pattern", "test"));

        private static DecisionSignal MakeDeadlineFired(long ordinal, DateTime occurredAtUtc, string deadlineName) =>
            new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: DecisionSignalKind.DeadlineFired,
                kindSchemaVersion: 1,
                occurredAtUtc: occurredAtUtc,
                sourceOrigin: "test",
                evidence: new Evidence(EvidenceKind.Synthetic, $"deadline:{deadlineName}:fired", "test"),
                payload: new System.Collections.Generic.Dictionary<string, string>
                {
                    [SignalPayloadKeys.Deadline] = deadlineName,
                });
    }
}
