using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Serialization;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests
{
    /// <summary>
    /// Plan §6 Fix 9 — <see cref="DecisionSignalKind.EspConfigDetected"/> populates the
    /// <see cref="DecisionState.SkipUserEsp"/> / <see cref="DecisionState.SkipDeviceEsp"/>
    /// facts. Set-once semantics: later signals with the same or different payload are no-ops
    /// once a fact is present (monotonic, analogous to <c>DeviceSetupEnteredUtc</c>).
    /// </summary>
    public sealed class EspConfigDetectedHandlerTests
    {
        [Fact]
        public void EspConfigDetected_populates_SkipUserEsp_and_SkipDeviceEsp_from_payload()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");

            var step = engine.Reduce(state, MakeSignal(
                ordinal: 3,
                skipUser: "true",
                skipDevice: "false"));

            Assert.NotNull(step.NewState.SkipUserEsp);
            Assert.True(step.NewState.SkipUserEsp!.Value);
            Assert.Equal(3, step.NewState.SkipUserEsp!.SourceSignalOrdinal);

            Assert.NotNull(step.NewState.SkipDeviceEsp);
            Assert.False(step.NewState.SkipDeviceEsp!.Value);
            Assert.Equal(3, step.NewState.SkipDeviceEsp!.SourceSignalOrdinal);

            Assert.True(step.Transition.Taken);
            Assert.Null(step.Transition.DeadEndReason);
            Assert.Equal(nameof(DecisionSignalKind.EspConfigDetected), step.Transition.Trigger);
            Assert.Empty(step.Effects);
        }

        [Fact]
        public void EspConfigDetected_leaves_stage_unchanged()
        {
            var engine = new DecisionEngine();
            var midFlight = DecisionState.CreateInitial("s", "t")
                .ToBuilder()
                .WithStage(SessionStage.EspDeviceSetup)
                .WithStepIndex(5)
                .WithLastAppliedSignalOrdinal(4)
                .Build();

            var step = engine.Reduce(midFlight, MakeSignal(
                ordinal: 5,
                skipUser: "false",
                skipDevice: "false"));

            Assert.Equal(SessionStage.EspDeviceSetup, step.NewState.Stage);
            Assert.Equal(6, step.NewState.StepIndex);
            Assert.Equal(5, step.NewState.LastAppliedSignalOrdinal);
        }

        [Fact]
        public void EspConfigDetected_is_setonce_laterSignalDoesNotOverwrite()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");

            var step1 = engine.Reduce(state, MakeSignal(
                ordinal: 1,
                skipUser: "false",
                skipDevice: "false"));

            // Second signal flips both values — must be ignored.
            var step2 = engine.Reduce(step1.NewState, MakeSignal(
                ordinal: 2,
                skipUser: "true",
                skipDevice: "true"));

            Assert.False(step2.NewState.SkipUserEsp!.Value);
            Assert.Equal(1, step2.NewState.SkipUserEsp!.SourceSignalOrdinal);
            Assert.False(step2.NewState.SkipDeviceEsp!.Value);
            Assert.Equal(1, step2.NewState.SkipDeviceEsp!.SourceSignalOrdinal);

            // The second signal still bumps bookkeeping (taken transition, no effects).
            Assert.True(step2.Transition.Taken);
            Assert.Equal(2, step2.NewState.StepIndex);
            Assert.Equal(2, step2.NewState.LastAppliedSignalOrdinal);
        }

        [Fact]
        public void EspConfigDetected_missingKeys_leavesFactsNull()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");

            var step = engine.Reduce(state, MakeSignal(
                ordinal: 1,
                skipUser: null,
                skipDevice: null));

            Assert.Null(step.NewState.SkipUserEsp);
            Assert.Null(step.NewState.SkipDeviceEsp);
            Assert.True(step.Transition.Taken);
            Assert.Equal(1, step.NewState.StepIndex);
        }

        [Fact]
        public void EspConfigDetected_partialPayload_setsOnlyKnownFact_leavesOtherNull()
        {
            // Realistic: registry has SkipUserStatusPage but SkipDeviceStatusPage key missing.
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");

            var step = engine.Reduce(state, MakeSignal(
                ordinal: 1,
                skipUser: "true",
                skipDevice: null));

            Assert.NotNull(step.NewState.SkipUserEsp);
            Assert.True(step.NewState.SkipUserEsp!.Value);
            Assert.Null(step.NewState.SkipDeviceEsp);
        }

        [Fact]
        public void EspConfigDetected_partialFirstSignal_secondSignalCanFillMissingFact()
        {
            // First signal sets only skipUser; second signal (different ordinal) can still fill
            // in skipDevice because set-once is per-fact, not per-signal.
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");

            var step1 = engine.Reduce(state, MakeSignal(
                ordinal: 1,
                skipUser: "true",
                skipDevice: null));

            var step2 = engine.Reduce(step1.NewState, MakeSignal(
                ordinal: 2,
                skipUser: null,
                skipDevice: "false"));

            Assert.NotNull(step2.NewState.SkipUserEsp);
            Assert.True(step2.NewState.SkipUserEsp!.Value);
            Assert.Equal(1, step2.NewState.SkipUserEsp!.SourceSignalOrdinal);

            Assert.NotNull(step2.NewState.SkipDeviceEsp);
            Assert.False(step2.NewState.SkipDeviceEsp!.Value);
            Assert.Equal(2, step2.NewState.SkipDeviceEsp!.SourceSignalOrdinal);
        }

        [Fact]
        public void EspConfigDetected_schemaVersion2_fallsThroughAsUnhandled()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");

            var step = engine.Reduce(state, MakeSignal(
                ordinal: 1,
                skipUser: "true",
                skipDevice: "false",
                schemaVersion: 2));

            Assert.False(step.Transition.Taken);
            Assert.Equal("unhandled_signal_kind:EspConfigDetected:v2", step.Transition.DeadEndReason);
        }

        [Fact]
        public void EspConfigDetected_stateRoundtripsThroughSerializer()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");

            var populated = engine.Reduce(state, MakeSignal(
                ordinal: 7,
                skipUser: "true",
                skipDevice: "false")).NewState;

            var json = StateSerializer.Serialize(populated);
            var roundtripped = StateSerializer.Deserialize(json);

            Assert.NotNull(roundtripped.SkipUserEsp);
            Assert.True(roundtripped.SkipUserEsp!.Value);
            Assert.Equal(7, roundtripped.SkipUserEsp!.SourceSignalOrdinal);

            Assert.NotNull(roundtripped.SkipDeviceEsp);
            Assert.False(roundtripped.SkipDeviceEsp!.Value);
            Assert.Equal(7, roundtripped.SkipDeviceEsp!.SourceSignalOrdinal);
        }

        [Fact]
        public void EspConfigDetected_initialState_facts_areNull()
        {
            var state = DecisionState.CreateInitial("s", "t");
            Assert.Null(state.SkipUserEsp);
            Assert.Null(state.SkipDeviceEsp);
        }

        private static DecisionSignal MakeSignal(
            long ordinal,
            string? skipUser,
            string? skipDevice,
            int schemaVersion = 1)
        {
            var payload = new Dictionary<string, string>(StringComparer.Ordinal);
            if (skipUser != null) payload[SignalPayloadKeys.SkipUserEsp] = skipUser;
            if (skipDevice != null) payload[SignalPayloadKeys.SkipDeviceEsp] = skipDevice;

            return new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: DecisionSignalKind.EspConfigDetected,
                kindSchemaVersion: schemaVersion,
                occurredAtUtc: new DateTime(2026, 4, 23, 18, 53, 21, DateTimeKind.Utc),
                sourceOrigin: "DeviceInfoCollector",
                evidence: new Evidence(
                    kind: EvidenceKind.Raw,
                    identifier: "esp_config_detected",
                    summary: $"SkipUser={skipUser ?? "unknown"}, SkipDevice={skipDevice ?? "unknown"}"),
                payload: payload);
        }
    }
}
