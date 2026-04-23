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
    /// Plan §5 Fix 6 — both-prerequisites-resolved routes through the non-terminal
    /// <see cref="SessionStage.Finalizing"/> stage with a FinalizingGrace deadline, emits a
    /// <c>phase_transition(FinalizingSetup)</c> declaration effect, and reaches
    /// <see cref="SessionStage.Completed"/> only when the deadline fires.
    /// </summary>
    public sealed class FinalizingStageTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 4, 23, 10, 0, 0, DateTimeKind.Utc);

        private static DecisionState InitialAwaitingDesktop(DecisionEngine engine)
        {
            // Build a state where ESP has reached AccountSetup, EspExiting arrived, Hello has
            // already resolved — the reducer parks in AwaitingDesktop waiting for the desktop.
            var state = DecisionState.CreateInitial("sess-fin", "tenant-fin");

            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0, null)).NewState;
            state = engine.Reduce(state, MakeSignal(1, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(1),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(2),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.EspExiting, T0.AddMinutes(3), null)).NewState;
            state = engine.Reduce(state, MakeSignal(4, DecisionSignalKind.HelloResolved, T0.AddMinutes(4),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloOutcome] = "Success" })).NewState;

            // Hello has resolved, Desktop hasn't — reducer should be in AwaitingDesktop.
            Assert.Equal(SessionStage.AwaitingDesktop, state.Stage);
            Assert.NotNull(state.HelloResolvedUtc);
            Assert.Null(state.DesktopArrivedUtc);
            return state;
        }

        private static DecisionSignal MakeSignal(
            long ordinal,
            DecisionSignalKind kind,
            DateTime occurredAtUtc,
            IReadOnlyDictionary<string, string>? payload)
        {
            return new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: kind,
                kindSchemaVersion: 1,
                occurredAtUtc: occurredAtUtc,
                sourceOrigin: "test",
                evidence: new Evidence(EvidenceKind.Synthetic, $"test-{kind}-{ordinal}", $"synthetic {kind}"),
                payload: payload);
        }

        [Fact]
        public void DesktopArrived_when_Hello_already_resolved_transitions_to_Finalizing_not_Completed()
        {
            var engine = new DecisionEngine();
            var state = InitialAwaitingDesktop(engine);

            var step = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.DesktopArrived, T0.AddMinutes(5), null));

            Assert.Equal(SessionStage.Finalizing, step.NewState.Stage);
            Assert.Null(step.NewState.Outcome); // NOT EnrollmentComplete yet — deferred until deadline fires
            Assert.NotNull(step.NewState.DesktopArrivedUtc);
            Assert.NotNull(step.NewState.HelloResolvedUtc);
            Assert.Single(step.NewState.Deadlines, d => d.Name == DeadlineNames.FinalizingGrace);
        }

        [Fact]
        public void DesktopArrived_when_Hello_resolved_emits_phase_transition_FinalizingSetup_effect()
        {
            var engine = new DecisionEngine();
            var state = InitialAwaitingDesktop(engine);

            var step = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.DesktopArrived, T0.AddMinutes(5), null));

            var phaseTransition = Assert.Single(
                step.Effects,
                e => e.Kind == DecisionEffectKind.EmitEventTimelineEntry
                     && e.Parameters != null
                     && e.Parameters.TryGetValue("eventType", out var et) && et == "phase_transition");
            Assert.Equal(nameof(EnrollmentPhase.FinalizingSetup), phaseTransition.Parameters!["phase"]);
        }

        [Fact]
        public void DesktopArrived_when_Hello_resolved_schedules_FinalizingGrace_deadline_effect()
        {
            var engine = new DecisionEngine();
            var state = InitialAwaitingDesktop(engine);
            var signalTime = T0.AddMinutes(5);

            var step = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.DesktopArrived, signalTime, null));

            var scheduleEffect = Assert.Single(step.Effects, e => e.Kind == DecisionEffectKind.ScheduleDeadline);
            Assert.NotNull(scheduleEffect.Deadline);
            Assert.Equal(DeadlineNames.FinalizingGrace, scheduleEffect.Deadline!.Name);
            Assert.Equal(signalTime.AddSeconds(5), scheduleEffect.Deadline.DueAtUtc);
        }

        [Fact]
        public void FinalizingGraceDeadline_fire_transitions_Finalizing_to_Completed_with_enrollment_complete()
        {
            var engine = new DecisionEngine();
            var state = InitialAwaitingDesktop(engine);
            state = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.DesktopArrived, T0.AddMinutes(5), null)).NewState;
            Assert.Equal(SessionStage.Finalizing, state.Stage);

            var step = engine.Reduce(
                state,
                MakeSignal(6, DecisionSignalKind.DeadlineFired, T0.AddMinutes(5).AddSeconds(5),
                    new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.FinalizingGrace }));

            Assert.Equal(SessionStage.Completed, step.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, step.NewState.Outcome);
            Assert.Empty(step.NewState.Deadlines);

            var terminalEffect = Assert.Single(
                step.Effects,
                e => e.Kind == DecisionEffectKind.EmitEventTimelineEntry
                     && e.Parameters != null
                     && e.Parameters.TryGetValue("eventType", out var et) && et == "enrollment_complete");
            Assert.NotNull(terminalEffect);
        }

        [Fact]
        public void HelloResolved_when_Desktop_already_arrived_also_routes_through_Finalizing()
        {
            // Mirror scenario: Desktop arrives first (before Hello resolves), reducer stays in
            // current stage; when HelloResolved arrives, reducer parks in Finalizing.
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("sess-fin-rev", "tenant-fin-rev");
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0, null)).NewState;
            state = engine.Reduce(state, MakeSignal(1, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(1),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.DesktopArrived, T0.AddMinutes(2), null)).NewState;
            Assert.NotNull(state.DesktopArrivedUtc);

            var step = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.HelloResolved, T0.AddMinutes(3),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloOutcome] = "Success" }));

            Assert.Equal(SessionStage.Finalizing, step.NewState.Stage);
            Assert.Null(step.NewState.Outcome);
            Assert.Single(step.NewState.Deadlines, d => d.Name == DeadlineNames.FinalizingGrace);
            Assert.Contains(step.Effects, e =>
                e.Kind == DecisionEffectKind.EmitEventTimelineEntry
                && e.Parameters != null
                && e.Parameters.TryGetValue("eventType", out var et) && et == "phase_transition");
        }

        [Fact]
        public void Finalizing_stage_is_not_marked_terminal_by_SessionStageExtensions()
        {
            // Guard against someone accidentally adding Finalizing to IsTerminal — that would
            // defeat the whole point of the grace window.
            Assert.False(SessionStage.Finalizing.IsTerminal());
            Assert.True(SessionStage.Completed.IsTerminal());
        }
    }
}
