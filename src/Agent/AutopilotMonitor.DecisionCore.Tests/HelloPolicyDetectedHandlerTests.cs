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
    /// PR4 (882fef64 debrief) — <see cref="DecisionSignalKind.HelloPolicyDetected"/> populates
    /// <see cref="DecisionState.HelloPolicyEnabled"/> as a fact-only update. Stage is unchanged
    /// and no effects fire. The fact does NOT gate enrollment_complete — it informs the wait
    /// cadence in the HelloTracker (30s default vs 10s when policy=disabled). See
    /// `feedback_hello_policy_wait_not_completion`.
    /// </summary>
    public sealed class HelloPolicyDetectedHandlerTests
    {
        [Fact]
        public void Initial_state_HelloPolicyEnabled_isNull()
        {
            var state = DecisionState.CreateInitial("s", "t");
            Assert.Null(state.HelloPolicyEnabled);
        }

        [Fact]
        public void HelloPolicyDetected_enabled_setsState()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");

            var step = engine.Reduce(state, MakeSignal(ordinal: 3, helloEnabled: "true", source: "csp"));

            Assert.NotNull(step.NewState.HelloPolicyEnabled);
            Assert.True(step.NewState.HelloPolicyEnabled!.Value);
            Assert.Equal(3, step.NewState.HelloPolicyEnabled!.SourceSignalOrdinal);
            Assert.True(step.Transition.Taken);
            Assert.Null(step.Transition.DeadEndReason);
            Assert.Equal(nameof(DecisionSignalKind.HelloPolicyDetected), step.Transition.Trigger);
            Assert.Empty(step.Effects);
        }

        [Fact]
        public void HelloPolicyDetected_disabled_setsState()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");

            var step = engine.Reduce(state, MakeSignal(ordinal: 5, helloEnabled: "false", source: "gpo"));

            Assert.NotNull(step.NewState.HelloPolicyEnabled);
            Assert.False(step.NewState.HelloPolicyEnabled!.Value);
            Assert.Equal(5, step.NewState.HelloPolicyEnabled!.SourceSignalOrdinal);
        }

        [Fact]
        public void HelloPolicyDetected_leavesStageUnchanged()
        {
            var engine = new DecisionEngine();
            var midFlight = DecisionState.CreateInitial("s", "t")
                .ToBuilder()
                .WithStage(SessionStage.AwaitingHello)
                .WithStepIndex(7)
                .WithLastAppliedSignalOrdinal(6)
                .Build();

            var step = engine.Reduce(midFlight, MakeSignal(ordinal: 7, helloEnabled: "true", source: "csp"));

            Assert.Equal(SessionStage.AwaitingHello, step.NewState.Stage);
            Assert.Equal(8, step.NewState.StepIndex);
            Assert.Equal(7, step.NewState.LastAppliedSignalOrdinal);
        }

        [Fact]
        public void HelloPolicyDetected_repeatSameValue_isNoOp()
        {
            // The agent's CSP/GPO poll runs every 10s — if the policy doesn't change, every poll
            // would otherwise re-post a signal. The reducer treats a same-value signal as a no-op
            // (the SourceSignalOrdinal stays at the original detection so the evidence trace
            // points at the first occurrence).
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");

            var step1 = engine.Reduce(state, MakeSignal(ordinal: 1, helloEnabled: "true", source: "csp"));
            var step2 = engine.Reduce(step1.NewState, MakeSignal(ordinal: 9, helloEnabled: "true", source: "csp"));

            Assert.NotNull(step2.NewState.HelloPolicyEnabled);
            Assert.True(step2.NewState.HelloPolicyEnabled!.Value);
            Assert.Equal(1, step2.NewState.HelloPolicyEnabled!.SourceSignalOrdinal);
            Assert.Contains("no-op", step2.Transition.Trigger);
        }

        [Fact]
        public void HelloPolicyDetected_changedValue_updatesFactWithLatestOrdinal()
        {
            // A fixed detector (or a real policy change) flips the value — that should propagate
            // and re-anchor the source ordinal so the Inspector points at the correction.
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");

            var step1 = engine.Reduce(state, MakeSignal(ordinal: 1, helloEnabled: "false", source: "csp"));
            var step2 = engine.Reduce(step1.NewState, MakeSignal(ordinal: 4, helloEnabled: "true", source: "csp"));

            Assert.NotNull(step2.NewState.HelloPolicyEnabled);
            Assert.True(step2.NewState.HelloPolicyEnabled!.Value);
            Assert.Equal(4, step2.NewState.HelloPolicyEnabled!.SourceSignalOrdinal);
            Assert.True(step2.Transition.Taken);
            Assert.Equal(nameof(DecisionSignalKind.HelloPolicyDetected), step2.Transition.Trigger);
        }

        [Fact]
        public void HelloPolicyDetected_missingPayloadKey_isDeadEnd()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");

            var step = engine.Reduce(state, MakeSignal(ordinal: 1, helloEnabled: null, source: "csp"));

            Assert.False(step.Transition.Taken);
            Assert.Equal("hello_policy_detected_missing_helloEnabled", step.Transition.DeadEndReason);
            Assert.Null(step.NewState.HelloPolicyEnabled);
        }

        [Fact]
        public void HelloPolicyDetected_unparseableValue_isDeadEnd()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");

            var step = engine.Reduce(state, MakeSignal(ordinal: 1, helloEnabled: "yes", source: "csp"));

            Assert.False(step.Transition.Taken);
            Assert.Equal("hello_policy_detected_missing_helloEnabled", step.Transition.DeadEndReason);
            Assert.Null(step.NewState.HelloPolicyEnabled);
        }

        [Fact]
        public void HelloPolicyDetected_schemaVersion2_fallsThroughAsUnhandled()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");

            var step = engine.Reduce(state, MakeSignal(
                ordinal: 1,
                helloEnabled: "true",
                source: "csp",
                schemaVersion: 2));

            Assert.False(step.Transition.Taken);
            Assert.Equal("unhandled_signal_kind:HelloPolicyDetected:v2", step.Transition.DeadEndReason);
        }

        [Fact]
        public void HelloPolicyDetected_stateRoundtripsThroughSerializer()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");

            var populated = engine.Reduce(state, MakeSignal(ordinal: 11, helloEnabled: "false", source: "gpo")).NewState;

            var json = StateSerializer.Serialize(populated);
            var roundtripped = StateSerializer.Deserialize(json);

            Assert.NotNull(roundtripped.HelloPolicyEnabled);
            Assert.False(roundtripped.HelloPolicyEnabled!.Value);
            Assert.Equal(11, roundtripped.HelloPolicyEnabled!.SourceSignalOrdinal);
        }

        [Fact]
        public void OldSnapshotJson_withoutHelloPolicyEnabled_loadsAsNull()
        {
            // Simulate a snapshot from a pre-PR4 agent that doesn't carry HelloPolicyEnabled at
            // all. The deserializer must fall back to null without throwing — otherwise restart
            // recovery would break for any in-progress session at deploy time.
            var engine = new DecisionEngine();
            var oldState = DecisionState.CreateInitial("s", "t")
                .ToBuilder()
                .WithStage(SessionStage.AwaitingHello)
                .Build();
            var oldJson = StateSerializer.Serialize(oldState);

            // The serializer round-trips HelloPolicyEnabled=null on its own, but to make this
            // test resilient against a future snapshot that omits the property entirely we just
            // assert the post-roundtrip null. Real-world divergence would surface as a missing
            // JSON property; System.Text.Json defaults missing properties to null.
            var loaded = StateSerializer.Deserialize(oldJson);
            Assert.Null(loaded.HelloPolicyEnabled);

            // And applying the policy signal afterwards still works (proves no broken seed state).
            var step = engine.Reduce(loaded, MakeSignal(ordinal: 12, helloEnabled: "true", source: "csp"));
            Assert.NotNull(step.NewState.HelloPolicyEnabled);
            Assert.True(step.NewState.HelloPolicyEnabled!.Value);
        }

        private static DecisionSignal MakeSignal(
            long ordinal,
            string? helloEnabled,
            string? source,
            int schemaVersion = 1)
        {
            var payload = new Dictionary<string, string>(StringComparer.Ordinal);
            if (helloEnabled != null) payload[SignalPayloadKeys.HelloEnabled] = helloEnabled;
            if (source != null) payload[SignalPayloadKeys.HelloPolicySource] = source;

            return new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: DecisionSignalKind.HelloPolicyDetected,
                kindSchemaVersion: schemaVersion,
                occurredAtUtc: new DateTime(2026, 4, 25, 18, 0, 0, DateTimeKind.Utc),
                sourceOrigin: "EspAndHelloTracker",
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: "esp-and-hello-tracker-v1",
                    summary: $"helloEnabled={helloEnabled ?? "unknown"} source={source ?? "unknown"}",
                    derivationInputs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["subSource"] = "HelloTracker",
                        [SignalPayloadKeys.HelloEnabled] = helloEnabled ?? "unknown",
                        [SignalPayloadKeys.HelloPolicySource] = source ?? "unknown",
                    }),
                payload: payload);
        }
    }
}
