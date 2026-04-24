using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests
{
    /// <summary>
    /// Plan §4.x M4.4.3 — <see cref="DecisionSignalKind.DeviceInfoCollected"/> and
    /// <see cref="DecisionSignalKind.AutopilotProfileRead"/> are diagnostic signals that
    /// previously fell through to <c>HandleUnhandledSignal</c> and produced
    /// <c>unhandled_signal_kind:*</c> dead-ends in the journal. They are now first-class
    /// neutral observations: bookkeeping advances, the transition is <c>Taken</c>, the stage
    /// is unchanged, no effects, no hypothesis impact.
    /// </summary>
    public sealed class DiagnosticSignalHandlerTests
    {
        [Theory]
        [InlineData(DecisionSignalKind.DeviceInfoCollected)]
        [InlineData(DecisionSignalKind.AutopilotProfileRead)]
        public void Diagnostic_signal_records_taken_transition_without_deadend(DecisionSignalKind kind)
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");

            var step = engine.Reduce(state, MakeSignal(kind, ordinal: 3));

            Assert.True(step.Transition.Taken);
            Assert.Null(step.Transition.DeadEndReason);
            Assert.Equal(kind.ToString(), step.Transition.Trigger);
        }

        [Theory]
        [InlineData(DecisionSignalKind.DeviceInfoCollected)]
        [InlineData(DecisionSignalKind.AutopilotProfileRead)]
        public void Diagnostic_signal_leaves_stage_and_hypotheses_unchanged(DecisionSignalKind kind)
        {
            var engine = new DecisionEngine();
            var midFlight = DecisionState.CreateInitial("s", "t")
                .ToBuilder()
                .WithStage(SessionStage.EspAccountSetup)
                .WithStepIndex(10)
                .WithLastAppliedSignalOrdinal(9)
                .Build();

            var step = engine.Reduce(midFlight, MakeSignal(kind, ordinal: 10));

            Assert.Equal(SessionStage.EspAccountSetup, step.NewState.Stage);
            // Diagnostic signals must not change any of the typed aggregates.
            Assert.Same(midFlight.ScenarioProfile, step.NewState.ScenarioProfile);
            Assert.Same(midFlight.ScenarioObservations, step.NewState.ScenarioObservations);
            Assert.Same(midFlight.ClassifierOutcomes, step.NewState.ClassifierOutcomes);
        }

        [Theory]
        [InlineData(DecisionSignalKind.DeviceInfoCollected)]
        [InlineData(DecisionSignalKind.AutopilotProfileRead)]
        public void Diagnostic_signal_advances_bookkeeping(DecisionSignalKind kind)
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t")
                .ToBuilder()
                .WithStepIndex(7)
                .WithLastAppliedSignalOrdinal(6)
                .Build();

            var step = engine.Reduce(state, MakeSignal(kind, ordinal: 7));

            Assert.Equal(8, step.NewState.StepIndex);
            Assert.Equal(7, step.NewState.LastAppliedSignalOrdinal);
        }

        [Theory]
        [InlineData(DecisionSignalKind.DeviceInfoCollected)]
        [InlineData(DecisionSignalKind.AutopilotProfileRead)]
        public void Diagnostic_signal_emits_no_effects(DecisionSignalKind kind)
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");

            var step = engine.Reduce(state, MakeSignal(kind));

            Assert.Empty(step.Effects);
        }

        [Theory]
        [InlineData(DecisionSignalKind.DeviceInfoCollected)]
        [InlineData(DecisionSignalKind.AutopilotProfileRead)]
        public void Diagnostic_signal_does_not_cancel_active_deadlines(DecisionSignalKind kind)
        {
            var engine = new DecisionEngine();
            var stateWithDeadline = DecisionState.CreateInitial("s", "t")
                .ToBuilder()
                .AddDeadline(new ActiveDeadline(
                    name: DeadlineNames.HelloSafety,
                    dueAtUtc: new DateTime(2026, 4, 20, 10, 5, 0, DateTimeKind.Utc),
                    firesSignalKind: DecisionSignalKind.DeadlineFired))
                .Build();

            var step = engine.Reduce(stateWithDeadline, MakeSignal(kind));

            Assert.Single(step.NewState.Deadlines);
            Assert.Equal(DeadlineNames.HelloSafety, step.NewState.Deadlines[0].Name);
        }

        [Fact]
        public void Diagnostic_signals_at_higher_schema_version_fall_through_to_unhandled()
        {
            // §2.2 L.6 — dispatch is keyed on (Kind, SchemaVersion). If a collector ever emits
            // a v2 variant before the reducer handles it, the kernel must dead-end (not
            // silently record), so replay flags the version gap.
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");

            var step = engine.Reduce(state, MakeSignal(DecisionSignalKind.DeviceInfoCollected, schemaVersion: 2));

            Assert.False(step.Transition.Taken);
            Assert.Equal("unhandled_signal_kind:DeviceInfoCollected:v2", step.Transition.DeadEndReason);
        }

        private static DecisionSignal MakeSignal(
            DecisionSignalKind kind,
            long ordinal = 0,
            int schemaVersion = 1,
            IReadOnlyDictionary<string, string>? payload = null) =>
            new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: kind,
                kindSchemaVersion: schemaVersion,
                occurredAtUtc: new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc),
                sourceOrigin: "test",
                evidence: new Evidence(EvidenceKind.Raw, $"test:{kind}", "test"),
                payload: payload);
    }
}
