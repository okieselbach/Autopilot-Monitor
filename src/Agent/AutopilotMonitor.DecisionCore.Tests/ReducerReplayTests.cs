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
    /// Codex follow-up #1 (recovery correctness) — verifies that
    /// <see cref="ReducerReplay.Replay"/> is a pure fold over the reducer kernel,
    /// enforces the ordinal monotonicity invariant, and is safe for tail-replay
    /// after a crash. See <c>.claude/plans/codex-01-recovery-signallog-replay.md</c>
    /// Phase 1 for the contract these tests protect.
    /// </summary>
    public sealed class ReducerReplayTests
    {
        // ----- Helpers -----

        private static DecisionSignal MakeSessionStarted(long ordinal) =>
            new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: DecisionSignalKind.SessionStarted,
                kindSchemaVersion: 1,
                occurredAtUtc: new DateTime(2026, 4, 24, 10, 0, 0, DateTimeKind.Utc),
                sourceOrigin: "replay-tests",
                evidence: new Evidence(EvidenceKind.Synthetic, "session:started", "init"));

        /// <summary>
        /// Stable stream used across multiple determinism tests. One SessionStarted
        /// followed by a couple of AppInstallCompleted signals (currently unhandled
        /// by the reducer — produce dead-end transitions, which is perfect for
        /// exercising the fold without depending on any particular handler semantics).
        /// </summary>
        private static IReadOnlyList<DecisionSignal> MakeCanonicalStream()
        {
            return new List<DecisionSignal>
            {
                MakeSessionStarted(0),
                new DecisionSignal(
                    sessionSignalOrdinal: 1,
                    sessionTraceOrdinal: 1,
                    kind: DecisionSignalKind.AppInstallCompleted,
                    kindSchemaVersion: 1,
                    occurredAtUtc: new DateTime(2026, 4, 24, 10, 0, 5, DateTimeKind.Utc),
                    sourceOrigin: "replay-tests",
                    evidence: new Evidence(EvidenceKind.Raw, "app:1", "installed")),
                new DecisionSignal(
                    sessionSignalOrdinal: 2,
                    sessionTraceOrdinal: 2,
                    kind: DecisionSignalKind.AppInstallCompleted,
                    kindSchemaVersion: 1,
                    occurredAtUtc: new DateTime(2026, 4, 24, 10, 0, 10, DateTimeKind.Utc),
                    sourceOrigin: "replay-tests",
                    evidence: new Evidence(EvidenceKind.Raw, "app:2", "installed")),
            };
        }

        // ----- Tests -----

        [Fact]
        public void Replay_EmptySignalStream_ReturnsSeedUnchanged()
        {
            var engine = new DecisionEngine();
            var seed = DecisionState.CreateInitial("s", "t");

            var result = ReducerReplay.Replay(engine, seed, Array.Empty<DecisionSignal>());

            Assert.Same(seed, result);
            Assert.Equal(0, result.StepIndex);
            Assert.Equal(-1, result.LastAppliedSignalOrdinal);
        }

        [Fact]
        public void Replay_SingleSignal_MatchesSingleReduce()
        {
            var engine = new DecisionEngine();
            var seed = DecisionState.CreateInitial("s", "t");
            var signal = MakeSessionStarted(0);

            var stepwise = engine.Reduce(seed, signal).NewState;
            var replayed = ReducerReplay.Replay(engine, seed, new[] { signal });

            Assert.Equal(stepwise.StepIndex, replayed.StepIndex);
            Assert.Equal(stepwise.LastAppliedSignalOrdinal, replayed.LastAppliedSignalOrdinal);
            Assert.Equal(stepwise.Stage, replayed.Stage);
        }

        [Fact]
        public void Replay_MultipleSignals_ProducesSameFinalStateAsStepwiseFold()
        {
            // This is the load-bearing determinism property — if this ever fails, the
            // recovery path can no longer trust a replay to reproduce the pre-crash state.
            var engine = new DecisionEngine();
            var seed = DecisionState.CreateInitial("s", "t");
            var signals = MakeCanonicalStream();

            // Reference: manual fold through Reduce.
            var stepwise = seed;
            foreach (var s in signals)
            {
                stepwise = engine.Reduce(stepwise, s).NewState;
            }

            var replayed = ReducerReplay.Replay(engine, seed, signals);

            Assert.Equal(stepwise.SessionId, replayed.SessionId);
            Assert.Equal(stepwise.TenantId, replayed.TenantId);
            Assert.Equal(stepwise.Stage, replayed.Stage);
            Assert.Equal(stepwise.Outcome, replayed.Outcome);
            Assert.Equal(stepwise.StepIndex, replayed.StepIndex);
            Assert.Equal(stepwise.LastAppliedSignalOrdinal, replayed.LastAppliedSignalOrdinal);
            Assert.Equal(stepwise.Deadlines.Count, replayed.Deadlines.Count);
        }

        [Fact]
        public void Replay_TailFromSnapshot_ReachesSameStateAsFullReplay()
        {
            // Recovery scenario: the orchestrator has a snapshot at ordinal=0 and a SignalLog
            // containing 0..2. Tail-replay (seed=snapshot, signals=[1,2]) must land on the
            // same state as a full replay from the initial seed.
            var engine = new DecisionEngine();
            var fresh = DecisionState.CreateInitial("s", "t");
            var signals = MakeCanonicalStream();

            var snapshotAfterFirst = engine.Reduce(fresh, signals[0]).NewState;
            var tail = new List<DecisionSignal> { signals[1], signals[2] };

            var fullReplay = ReducerReplay.Replay(engine, fresh, signals);
            var tailReplay = ReducerReplay.Replay(engine, snapshotAfterFirst, tail);

            Assert.Equal(fullReplay.StepIndex, tailReplay.StepIndex);
            Assert.Equal(fullReplay.LastAppliedSignalOrdinal, tailReplay.LastAppliedSignalOrdinal);
            Assert.Equal(fullReplay.Stage, tailReplay.Stage);
        }

        [Fact]
        public void Replay_SignalOrdinalAtOrBelowSeed_ThrowsMonotonicityViolation()
        {
            // Guard against re-applying a signal that is already baked into the seed.
            // Without this check the reducer would regress LastAppliedSignalOrdinal.
            var engine = new DecisionEngine();
            var seedAfterOrdinal5 = DecisionState.CreateInitial("s", "t")
                .ToBuilder()
                .WithLastAppliedSignalOrdinal(5)
                .Build();
            var stale = MakeSessionStarted(5); // equal to seed — must reject

            var ex = Assert.Throws<InvalidOperationException>(() =>
                ReducerReplay.Replay(engine, seedAfterOrdinal5, new[] { stale }));

            Assert.Contains("non-monotonic signal ordinal", ex.Message);
        }

        [Fact]
        public void Replay_NonMonotonicStream_ThrowsMidFold()
        {
            // Two-signal stream where the second ordinal is less than the first.
            // Replay must throw on the offending signal — never silently accept.
            var engine = new DecisionEngine();
            var seed = DecisionState.CreateInitial("s", "t");
            var signals = new[]
            {
                MakeSessionStarted(3),
                MakeSessionStarted(2), // out-of-order
            };

            var ex = Assert.Throws<InvalidOperationException>(() =>
                ReducerReplay.Replay(engine, seed, signals));

            Assert.Contains("non-monotonic signal ordinal", ex.Message);
            Assert.Contains("2", ex.Message);
        }

        [Fact]
        public void Replay_NullArgs_ThrowArgumentNullException()
        {
            var engine = new DecisionEngine();
            var seed = DecisionState.CreateInitial("s", "t");

            Assert.Throws<ArgumentNullException>(() =>
                ReducerReplay.Replay(null!, seed, Array.Empty<DecisionSignal>()));
            Assert.Throws<ArgumentNullException>(() =>
                ReducerReplay.Replay(engine, null!, Array.Empty<DecisionSignal>()));
            Assert.Throws<ArgumentNullException>(() =>
                ReducerReplay.Replay(engine, seed, null!));
        }
    }
}
