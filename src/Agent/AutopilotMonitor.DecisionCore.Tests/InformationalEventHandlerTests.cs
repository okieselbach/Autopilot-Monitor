using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests
{
    /// <summary>
    /// Single-rail refactor plan §1.3 / §4.1b — <see cref="DecisionSignalKind.InformationalEvent"/>
    /// is the generic pass-through kind that carries an EnrollmentEvent payload through the
    /// engine without touching DecisionState. The reducer validates the mandatory keys,
    /// records a taken transition, and emits exactly one
    /// <see cref="DecisionEffectKind.EmitEventTimelineEntry"/> effect whose parameters are the
    /// signal payload verbatim.
    /// </summary>
    public sealed class InformationalEventHandlerTests
    {
        private static readonly DateTime At = new DateTime(2026, 4, 23, 10, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Valid_payload_records_taken_transition_without_deadend()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");

            var step = engine.Reduce(state, MakeSignal(payload: MinimalPayload()));

            Assert.True(step.Transition.Taken);
            Assert.Null(step.Transition.DeadEndReason);
            Assert.Equal(nameof(DecisionSignalKind.InformationalEvent), step.Transition.Trigger);
        }

        [Fact]
        public void Valid_payload_emits_exactly_one_EmitEventTimelineEntry_effect()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");

            var step = engine.Reduce(state, MakeSignal(payload: MinimalPayload()));

            Assert.Single(step.Effects);
            Assert.Equal(DecisionEffectKind.EmitEventTimelineEntry, step.Effects[0].Kind);
        }

        [Fact]
        public void Effect_parameters_are_the_signal_payload_verbatim()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");
            var payload = new Dictionary<string, string>
            {
                [SignalPayloadKeys.EventType] = "ntp_time_check",
                [SignalPayloadKeys.Source] = "Network",
                [SignalPayloadKeys.Severity] = "Warning",
                [SignalPayloadKeys.Message] = "NTP offset -124.02s",
                [SignalPayloadKeys.ImmediateUpload] = "false",
                ["offsetSeconds"] = "-124.02",
                ["ntpServer"] = "time.windows.com",
            };

            var step = engine.Reduce(state, MakeSignal(payload: payload));

            Assert.Same(payload, step.Effects[0].Parameters);
        }

        [Fact]
        public void Valid_payload_leaves_stage_and_hypotheses_unchanged()
        {
            var engine = new DecisionEngine();
            var midFlight = DecisionState.CreateInitial("s", "t")
                .ToBuilder()
                .WithStage(SessionStage.EspAccountSetup)
                .WithStepIndex(10)
                .WithLastAppliedSignalOrdinal(9)
                .Build();

            var step = engine.Reduce(midFlight, MakeSignal(ordinal: 10, payload: MinimalPayload()));

            Assert.Equal(SessionStage.EspAccountSetup, step.NewState.Stage);
            Assert.Same(midFlight.EnrollmentType, step.NewState.EnrollmentType);
            Assert.Same(midFlight.WhiteGloveSealing, step.NewState.WhiteGloveSealing);
            Assert.Same(midFlight.DeviceOnlyDeployment, step.NewState.DeviceOnlyDeployment);
        }

        [Fact]
        public void Valid_payload_advances_bookkeeping()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t")
                .ToBuilder()
                .WithStepIndex(7)
                .WithLastAppliedSignalOrdinal(6)
                .Build();

            var step = engine.Reduce(state, MakeSignal(ordinal: 7, payload: MinimalPayload()));

            Assert.Equal(8, step.NewState.StepIndex);
            Assert.Equal(7, step.NewState.LastAppliedSignalOrdinal);
        }

        [Fact]
        public void Valid_payload_does_not_cancel_active_deadlines()
        {
            var engine = new DecisionEngine();
            var stateWithDeadline = DecisionState.CreateInitial("s", "t")
                .ToBuilder()
                .AddDeadline(new ActiveDeadline(
                    name: DeadlineNames.HelloSafety,
                    dueAtUtc: new DateTime(2026, 4, 23, 10, 5, 0, DateTimeKind.Utc),
                    firesSignalKind: DecisionSignalKind.DeadlineFired))
                .Build();

            var step = engine.Reduce(stateWithDeadline, MakeSignal(payload: MinimalPayload()));

            Assert.Single(step.NewState.Deadlines);
            Assert.Equal(DeadlineNames.HelloSafety, step.NewState.Deadlines[0].Name);
        }

        [Fact]
        public void Null_payload_produces_deadend_for_missing_eventType()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");

            var step = engine.Reduce(state, MakeSignal(payload: null));

            Assert.False(step.Transition.Taken);
            Assert.Equal($"informational_event_missing_{SignalPayloadKeys.EventType}", step.Transition.DeadEndReason);
            Assert.Empty(step.Effects);
        }

        [Fact]
        public void Missing_eventType_produces_deadend()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");
            var payload = new Dictionary<string, string>
            {
                [SignalPayloadKeys.Source] = "Network",
            };

            var step = engine.Reduce(state, MakeSignal(payload: payload));

            Assert.False(step.Transition.Taken);
            Assert.Equal($"informational_event_missing_{SignalPayloadKeys.EventType}", step.Transition.DeadEndReason);
            Assert.Empty(step.Effects);
        }

        [Fact]
        public void Empty_eventType_produces_deadend()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");
            var payload = new Dictionary<string, string>
            {
                [SignalPayloadKeys.EventType] = string.Empty,
                [SignalPayloadKeys.Source] = "Network",
            };

            var step = engine.Reduce(state, MakeSignal(payload: payload));

            Assert.False(step.Transition.Taken);
            Assert.Equal($"informational_event_missing_{SignalPayloadKeys.EventType}", step.Transition.DeadEndReason);
        }

        [Fact]
        public void Missing_source_produces_deadend()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");
            var payload = new Dictionary<string, string>
            {
                [SignalPayloadKeys.EventType] = "ntp_time_check",
            };

            var step = engine.Reduce(state, MakeSignal(payload: payload));

            Assert.False(step.Transition.Taken);
            Assert.Equal($"informational_event_missing_{SignalPayloadKeys.Source}", step.Transition.DeadEndReason);
            Assert.Empty(step.Effects);
        }

        [Fact]
        public void Empty_source_produces_deadend()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");
            var payload = new Dictionary<string, string>
            {
                [SignalPayloadKeys.EventType] = "ntp_time_check",
                [SignalPayloadKeys.Source] = string.Empty,
            };

            var step = engine.Reduce(state, MakeSignal(payload: payload));

            Assert.False(step.Transition.Taken);
            Assert.Equal($"informational_event_missing_{SignalPayloadKeys.Source}", step.Transition.DeadEndReason);
        }

        [Fact]
        public void Deadend_signal_still_advances_bookkeeping()
        {
            // Even a malformed signal counts as processed — the kernel's LastAppliedSignalOrdinal
            // must move so the same ordinal cannot be re-applied on replay.
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t")
                .ToBuilder()
                .WithStepIndex(4)
                .WithLastAppliedSignalOrdinal(3)
                .Build();

            var step = engine.Reduce(state, MakeSignal(ordinal: 4, payload: null));

            Assert.Equal(5, step.NewState.StepIndex);
            Assert.Equal(4, step.NewState.LastAppliedSignalOrdinal);
        }

        [Fact]
        public void Higher_schema_version_falls_through_to_unhandled()
        {
            // §2.2 L.6 — dispatch is keyed on (Kind, SchemaVersion). A future v2 of the kind
            // without a matching reducer must DeadEnd so replay flags the version gap.
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");

            var step = engine.Reduce(
                state,
                MakeSignal(payload: MinimalPayload(), schemaVersion: 2));

            Assert.False(step.Transition.Taken);
            Assert.Equal("unhandled_signal_kind:InformationalEvent:v2", step.Transition.DeadEndReason);
        }

        private static IReadOnlyDictionary<string, string> MinimalPayload() =>
            new Dictionary<string, string>
            {
                [SignalPayloadKeys.EventType] = "ntp_time_check",
                [SignalPayloadKeys.Source] = "Network",
            };

        private static DecisionSignal MakeSignal(
            long ordinal = 1,
            int schemaVersion = 1,
            IReadOnlyDictionary<string, string>? payload = null) =>
            new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: DecisionSignalKind.InformationalEvent,
                kindSchemaVersion: schemaVersion,
                occurredAtUtc: At,
                sourceOrigin: "test",
                evidence: new Evidence(EvidenceKind.Raw, "test:informational", "test"),
                payload: payload);
    }
}
