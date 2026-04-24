using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.DecisionCore.Tests.Harness;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests
{
    /// <summary>
    /// Plan §4 M2 gate: "Harness durchläuft leeren Signal-Stream deterministisch."
    /// M3 adds scenario-replay tests against real fixtures once the reducer is live.
    /// </summary>
    public sealed class HarnessTests
    {
        [Fact]
        public void Replay_emptySignalStream_producesInitialState()
        {
            var harness = new ReplayHarness(new DecisionEngine());

            var result = harness.Replay(
                sessionId: "session-empty",
                tenantId: "tenant-empty",
                signals: Array.Empty<DecisionSignal>());

            Assert.Equal(SessionStage.SessionStarted, result.FinalState.Stage);
            Assert.Null(result.FinalState.Outcome);
            Assert.Equal(0, result.FinalState.StepIndex);
            Assert.Equal(-1, result.FinalState.LastAppliedSignalOrdinal);
            Assert.Empty(result.Transitions);
        }

        [Fact]
        public void Replay_emptyStream_hashIsDeterministic_sameInput_sameHash()
        {
            var harness = new ReplayHarness(new DecisionEngine());

            var hash1 = harness.Replay("s", "t", Array.Empty<DecisionSignal>()).FinalStepHash;
            var hash2 = harness.Replay("s", "t", Array.Empty<DecisionSignal>()).FinalStepHash;

            Assert.Equal(hash1, hash2);
            Assert.Equal(16, hash1.Length);
        }

        [Fact]
        public void Replay_differentSessionIds_produceDifferentHashes()
        {
            var harness = new ReplayHarness(new DecisionEngine());

            var hashA = harness.Replay("session-A", "t", Array.Empty<DecisionSignal>()).FinalStepHash;
            var hashB = harness.Replay("session-B", "t", Array.Empty<DecisionSignal>()).FinalStepHash;

            Assert.NotEqual(hashA, hashB);
        }

        [Fact]
        public void Replay_withNullSignals_throws()
        {
            var harness = new ReplayHarness(new DecisionEngine());
            Assert.Throws<ArgumentNullException>(() => harness.Replay("s", "t", null!));
        }

        [Fact]
        public void Replay_singleSessionStartedSignal_recordsOneTakenTransition()
        {
            var harness = new ReplayHarness(new DecisionEngine());
            var signal = new DecisionSignal(
                sessionSignalOrdinal: 0,
                sessionTraceOrdinal: 0,
                kind: DecisionSignalKind.SessionStarted,
                kindSchemaVersion: 1,
                occurredAtUtc: DateTime.UtcNow,
                sourceOrigin: "harness",
                evidence: new Evidence(EvidenceKind.Synthetic, "session:started", "init"));

            var result = harness.Replay("s", "t", new List<DecisionSignal> { signal });

            Assert.Single(result.Transitions);
            Assert.True(result.Transitions[0].Taken);
            Assert.Equal(SessionStage.SessionStarted, result.FinalState.Stage);
            Assert.Equal(1, result.FinalState.StepIndex);
            Assert.Equal(0, result.FinalState.LastAppliedSignalOrdinal);
        }

        [Fact]
        public void Replay_unknownSchemaVersion_recordsDeadEnd_stateUnchanged()
        {
            // Codex follow-up #4 wired AppInstallCompleted / AppInstallFailed, so every
            // DecisionSignalKind now has at least one handler. The dead-end path remains
            // reachable through an unknown schema version — the forward-compat surface the
            // reducer guards per plan §2.5. A v2 SessionStarted arriving at a v1-aware
            // backend is treated as an unhandled kind dispatch.
            var harness = new ReplayHarness(new DecisionEngine());
            var signal = new DecisionSignal(
                sessionSignalOrdinal: 0,
                sessionTraceOrdinal: 0,
                kind: DecisionSignalKind.SessionStarted,
                kindSchemaVersion: 99,
                occurredAtUtc: DateTime.UtcNow,
                sourceOrigin: "harness",
                evidence: new Evidence(EvidenceKind.Raw, "session:future", "test"));

            var result = harness.Replay("s", "t", new List<DecisionSignal> { signal });

            var t = Assert.Single(result.Transitions);
            Assert.False(t.Taken);
            Assert.StartsWith("unhandled_signal_kind", t.DeadEndReason);
            Assert.Equal(SessionStage.SessionStarted, result.FinalState.Stage);
        }
    }
}
