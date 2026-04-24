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
        public void Replay_OnTransitionCallback_InvokedOncePerSignalInOrder()
        {
            // Codex follow-up (post-#50 #C): the onTransition overload exposes each step's
            // transition to callers so they can rematerialise downstream artefacts (e.g.,
            // the JournalWriter tail after a BEHIND-the-log crash). Invocation contract:
            //   1. exactly one callback per signal,
            //   2. invoked BEFORE the state advances to the next signal,
            //   3. StepIndex strictly increasing with the signal order.
            var engine = new DecisionEngine();
            var seed = DecisionState.CreateInitial("s", "t");
            var signals = MakeCanonicalStream();

            var captured = new List<DecisionTransition>();
            var result = ReducerReplay.Replay(
                engine, seed, signals, onTransition: captured.Add);

            Assert.Equal(signals.Count, captured.Count);
            for (var i = 0; i < captured.Count; i++)
            {
                // Transitions come out in signal order and ordinals are strictly increasing.
                if (i > 0)
                {
                    Assert.True(
                        captured[i].StepIndex > captured[i - 1].StepIndex,
                        $"StepIndex regression between captured[{i - 1}]={captured[i - 1].StepIndex} and captured[{i}]={captured[i].StepIndex}.");
                }
                Assert.Equal(signals[i].SessionSignalOrdinal, captured[i].SignalOrdinalRef);
            }

            // Final state after the callback-capturing fold matches the plain 3-arg fold.
            var plain = ReducerReplay.Replay(engine, seed, signals);
            Assert.Equal(plain.StepIndex, result.StepIndex);
            Assert.Equal(plain.LastAppliedSignalOrdinal, result.LastAppliedSignalOrdinal);
            Assert.Equal(plain.Stage, result.Stage);
        }

        [Fact]
        public void Replay_OnTransitionCallback_NullIsEquivalentTo3ArgOverload()
        {
            // The nullable callback must behave like the 3-arg overload when omitted,
            // preserving zero-side-effect determinism for callers that only need state.
            var engine = new DecisionEngine();
            var seed = DecisionState.CreateInitial("s", "t");
            var signals = MakeCanonicalStream();

            var withCallback = ReducerReplay.Replay(engine, seed, signals, onTransition: null);
            var without = ReducerReplay.Replay(engine, seed, signals);

            Assert.Equal(without.StepIndex, withCallback.StepIndex);
            Assert.Equal(without.LastAppliedSignalOrdinal, withCallback.LastAppliedSignalOrdinal);
            Assert.Equal(without.Stage, withCallback.Stage);
        }

        [Fact]
        public void Replay_OnTransitionCallbackThrows_AbortsReplayAndPropagates()
        {
            // Callback exceptions must not be swallowed — they indicate a downstream
            // persistence failure (e.g., journal disk full during rebuild). The caller
            // decides whether to escalate to quarantine or retry.
            var engine = new DecisionEngine();
            var seed = DecisionState.CreateInitial("s", "t");
            var signals = MakeCanonicalStream();

            var invocations = 0;
            var ex = Assert.Throws<InvalidOperationException>(() =>
                ReducerReplay.Replay(engine, seed, signals, onTransition: _ =>
                {
                    invocations++;
                    if (invocations == 2) throw new InvalidOperationException("simulated disk-full");
                }));

            Assert.Contains("simulated disk-full", ex.Message);
            Assert.Equal(2, invocations); // aborted before the 3rd signal
        }

        [Theory]
        // Each row nulls exactly one required argument; all three must throw ArgumentNullException.
        // Splitting into a Theory gives per-argument failure isolation so a regression that only
        // affects one parameter position is identified at once.
        [InlineData(0)] // engine
        [InlineData(1)] // seed
        [InlineData(2)] // signals
        public void Replay_NullArgs_ThrowArgumentNullException(int nullPosition)
        {
            var engine = nullPosition == 0 ? null : new DecisionEngine();
            var seed = nullPosition == 1 ? null : DecisionState.CreateInitial("s", "t");
            var signals = nullPosition == 2 ? null : Array.Empty<DecisionSignal>();

            Assert.Throws<ArgumentNullException>(() => ReducerReplay.Replay(engine!, seed!, signals!));
        }
    }
}
