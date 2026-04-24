using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests
{
    /// <summary>
    /// Codex follow-up #2 — reducer handler for the synthetic
    /// <see cref="DecisionSignalKind.EffectInfrastructureFailure"/> signal that the
    /// EffectRunner posts when a critical deadline effect cannot be delivered.
    /// These tests pin the terminal-transition contract so the orchestrator never
    /// again observes <see cref="EffectRunResult.SessionMustAbort"/> without a
    /// matching state-machine transition.
    /// </summary>
    public sealed class EffectInfrastructureFailureHandlerTests
    {
        private static DecisionSignal MakeSignal(
            string? reason = "timer_infrastructure_failure: InvalidOperationException: timer-broken",
            long ordinal = 5)
        {
            var payload = new Dictionary<string, string>(StringComparer.Ordinal);
            if (reason != null) payload["reason"] = reason;
            payload["failingEffect"] = "ScheduleDeadline";

            return new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: DecisionSignalKind.EffectInfrastructureFailure,
                kindSchemaVersion: 1,
                occurredAtUtc: new DateTime(2026, 4, 24, 10, 0, 0, DateTimeKind.Utc),
                sourceOrigin: "effectrunner:critical:ScheduleDeadline",
                evidence: new Evidence(
                    EvidenceKind.Synthetic,
                    "effect_infrastructure_failure:ScheduleDeadline",
                    "Critical effect ScheduleDeadline failed"),
                payload: payload);
        }

        [Fact]
        public void Handler_transitions_to_Failed_with_EnrollmentFailed_outcome()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t")
                .ToBuilder()
                .WithStage(SessionStage.AwaitingHello)
                .WithStepIndex(4)
                .WithLastAppliedSignalOrdinal(4)
                .Build();

            var step = engine.Reduce(state, MakeSignal(ordinal: 5));

            Assert.True(step.Transition.Taken);
            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentFailed, step.NewState.Outcome);
            Assert.Equal(5, step.NewState.StepIndex);
            Assert.Equal(5, step.NewState.LastAppliedSignalOrdinal);
            Assert.Equal(
                nameof(DecisionSignalKind.EffectInfrastructureFailure),
                step.Transition.Trigger);
        }

        [Fact]
        public void Handler_clears_active_deadlines()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t")
                .ToBuilder()
                .WithStage(SessionStage.AwaitingHello)
                .AddDeadline(new ActiveDeadline(
                    name: "hello_safety",
                    dueAtUtc: new DateTime(2026, 4, 24, 10, 5, 0, DateTimeKind.Utc),
                    firesSignalKind: DecisionSignalKind.DeadlineFired))
                .AddDeadline(new ActiveDeadline(
                    name: "classifier_tick",
                    dueAtUtc: new DateTime(2026, 4, 24, 10, 10, 0, DateTimeKind.Utc),
                    firesSignalKind: DecisionSignalKind.DeadlineFired))
                .Build();

            var step = engine.Reduce(state, MakeSignal());

            Assert.Empty(step.NewState.Deadlines);
        }

        [Fact]
        public void Handler_emits_enrollment_failed_timeline_event_with_reason()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");

            var step = engine.Reduce(state, MakeSignal(reason: "custom-reason-abc"));

            Assert.Single(step.Effects);
            var effect = step.Effects[0];
            Assert.Equal(DecisionEffectKind.EmitEventTimelineEntry, effect.Kind);
            Assert.NotNull(effect.Parameters);
            Assert.Equal("enrollment_failed", effect.Parameters!["eventType"]);
            Assert.Equal("custom-reason-abc", effect.Parameters["reason"]);
        }

        [Fact]
        public void Handler_falls_back_to_default_reason_when_payload_missing_key()
        {
            // Payload without a "reason" key — reducer must still produce a valid
            // enrollment_failed event rather than leaving reason null/empty.
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");
            var signal = MakeSignal(reason: null); // helper strips the "reason" key when null

            var step = engine.Reduce(state, signal);

            Assert.Single(step.Effects);
            Assert.Equal("effect_infrastructure_failure", step.Effects[0].Parameters!["reason"]);
        }

        [Fact]
        public void Handler_from_terminal_state_keeps_Failed_outcome()
        {
            // Defensive: double-post (e.g. two critical effects in one EffectRun) must not
            // regress the terminal outcome or leak deadlines back in.
            var engine = new DecisionEngine();
            var alreadyFailed = DecisionState.CreateInitial("s", "t")
                .ToBuilder()
                .WithStage(SessionStage.Failed)
                .WithOutcome(SessionOutcome.EnrollmentFailed)
                .WithStepIndex(8)
                .WithLastAppliedSignalOrdinal(7)
                .Build();

            var step = engine.Reduce(alreadyFailed, MakeSignal(ordinal: 8));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentFailed, step.NewState.Outcome);
            Assert.Empty(step.NewState.Deadlines);
        }
    }
}
