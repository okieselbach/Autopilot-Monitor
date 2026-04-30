using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Classifiers;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.DecisionCore.Tests.Harness;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests.Scenarios
{
    /// <summary>
    /// Plan §4 M3.3 — WhiteGlove Part 1 scenarios. Uses the classifier-aware harness so
    /// RunClassifier effects are executed synchronously and the resulting verdicts feed
    /// back into the reducer.
    /// </summary>
    public sealed class WhiteGlovePart1ScenarioTests : ScenarioTestBase
    {
        private static ClassifierAwareReplayHarness NewHarness() =>
            new ClassifierAwareReplayHarness(
                new DecisionEngine(),
                new Dictionary<string, IClassifier>
                {
                    [WhiteGloveSealingClassifier.ClassifierId] = new WhiteGloveSealingClassifier(),
                });

        private static ReplayResult RunWgFixture(string fixture, string sessionId, string tenantId)
        {
            var signals = LoadFixture(fixture);
            return NewHarness().Replay(sessionId, tenantId, signals);
        }

        [Fact]
        public void WhiteGloveInline_shellCoreAlone_sealsWhiteGlove()
        {
            var result = RunWgFixture("whiteglove-inline-v1.jsonl", "session-anon-0010", "tenant-anon-0010");

            Assert.Equal(SessionStage.WhiteGloveSealed, result.FinalState.Stage);
            Assert.Equal(SessionOutcome.WhiteGlovePart1Sealed, result.FinalState.Outcome);
            Assert.Equal(HypothesisLevel.Confirmed, result.FinalState.ClassifierOutcomes.WhiteGloveSealing.Level);
            Assert.Equal(80, result.FinalState.ClassifierOutcomes.WhiteGloveSealing.Score);
            Assert.NotNull(result.FinalState.ScenarioObservations.ShellCoreWhiteGloveSuccessSeen);
            Assert.True(result.FinalState.ScenarioObservations.ShellCoreWhiteGloveSuccessSeen!.Value);
            Assert.Empty(result.FinalState.Deadlines);
        }

        [Fact]
        public void WhiteGloveSignalCorrelated_patternThenShellCore_sealsAfterSecondSignal()
        {
            var harness = NewHarness();
            var signals = LoadFixture("whiteglove-signal-correlated-v1.jsonl");
            var result = harness.Replay("session-anon-0011", "tenant-anon-0011", signals);

            Assert.Equal(SessionStage.WhiteGloveSealed, result.FinalState.Stage);
            Assert.Equal(SessionOutcome.WhiteGlovePart1Sealed, result.FinalState.Outcome);
            Assert.Equal(HypothesisLevel.Confirmed, result.FinalState.ClassifierOutcomes.WhiteGloveSealing.Level);
            Assert.True(result.FinalState.ClassifierOutcomes.WhiteGloveSealing.Score >= WhiteGloveSealingClassifier.HighThreshold);

            // Option 3 fast-path (WG Part 1 graceful-exit hardening, 2026-04-30): the
            // ShellCoreSuccess handler now classifies inline and transitions to
            // WhiteGloveSealed in a single reducer step, so it does NOT emit a RunClassifier
            // effect. Only the initial SealingPattern arrival emits RunClassifier (1 run);
            // the ClassifierTick that fires between the two WG signals re-evaluates the
            // unchanged snapshot and is anti-loop-skipped (1 skip). The second classifier
            // verdict is computed inline by the engine — invisible to the harness counters.
            var runs = harness.ClassifierRunStats.TryGetValue("whiteglove-sealing:run", out var r) ? r : 0;
            var skipped = harness.ClassifierRunStats.TryGetValue("whiteglove-sealing:skipped_by_antiloop", out var s) ? s : 0;
            Assert.Equal(1, runs);
            Assert.Equal(1, skipped);
        }

        [Fact]
        public void WhiteGloveFalsePositive_LateAadjAfterSealingPattern_rejectsWg_completesClassic()
        {
            var result = RunWgFixture("whiteglove-false-positive-v1.jsonl", "session-anon-0012", "tenant-anon-0012");

            // The session must NOT end in WhiteGloveSealed.
            Assert.NotEqual(SessionStage.WhiteGloveSealed, result.FinalState.Stage);
            Assert.Equal(SessionStage.Completed, result.FinalState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, result.FinalState.Outcome);

            // WhiteGlove verdict should be Rejected (hard-excluded by AadJoinedWithUser).
            Assert.Equal(HypothesisLevel.Rejected, result.FinalState.ClassifierOutcomes.WhiteGloveSealing.Level);
            Assert.Equal(0, result.FinalState.ClassifierOutcomes.WhiteGloveSealing.Score);

            // The late-AADJ fact + Hello + Desktop are all recorded.
            Assert.NotNull(result.FinalState.ScenarioObservations.AadUserJoinWithUserObserved);
            Assert.True(result.FinalState.ScenarioObservations.AadUserJoinWithUserObserved!.Value);
            Assert.NotNull(result.FinalState.HelloResolvedUtc);
            Assert.NotNull(result.FinalState.DesktopArrivedUtc);
            Assert.Equal("Success", result.FinalState.HelloOutcome!.Value);
        }

        [Fact]
        public void ClassifierAntiLoop_sameSnapshot_runsOnceThenSkips()
        {
            var harness = NewHarness();
            var signals = LoadFixture("whiteglove-antiloop-v1.jsonl");
            var result = harness.Replay("session-anon-0013", "tenant-anon-0013", signals);

            // SealingPattern was seen but insufficient alone for Confirmed.
            Assert.NotEqual(SessionStage.WhiteGloveSealed, result.FinalState.Stage);
            Assert.Equal(HypothesisLevel.Weak, result.FinalState.ClassifierOutcomes.WhiteGloveSealing.Level);
            Assert.Equal(40, result.FinalState.ClassifierOutcomes.WhiteGloveSealing.Score);

            var runs = harness.ClassifierRunStats.TryGetValue("whiteglove-sealing:run", out var r) ? r : 0;
            var skipped = harness.ClassifierRunStats.TryGetValue("whiteglove-sealing:skipped_by_antiloop", out var s) ? s : 0;
            Assert.Equal(1, runs);
            Assert.Equal(2, skipped);
        }

        // ============================================================ Option 3 — fast-path
        // (WG Part 1 graceful-exit hardening, 2026-04-30). The strong WG signal
        // (ShellCoreSuccess) now classifies inline and transitions to WhiteGloveSealed in a
        // single reducer step; no RunClassifier effect is emitted on the fast path.

        [Fact]
        public void FastPath_shellCoreSuccess_emits_no_runClassifier_effect()
        {
            var harness = NewHarness();
            var signals = LoadFixture("whiteglove-inline-v1.jsonl");
            var result = harness.Replay("session-anon-0014-fp1", "tenant-anon-0014", signals);

            Assert.Equal(SessionStage.WhiteGloveSealed, result.FinalState.Stage);
            Assert.Equal(SessionOutcome.WhiteGlovePart1Sealed, result.FinalState.Outcome);

            // The fast-path classifies inline — no RunClassifier effect should have been
            // posted, so the harness's run/skip counters stay at zero. (Compare against
            // the slow-path correlated test which records 1 run + 1 skip.)
            var runs = harness.ClassifierRunStats.TryGetValue("whiteglove-sealing:run", out var r) ? r : 0;
            var skipped = harness.ClassifierRunStats.TryGetValue("whiteglove-sealing:skipped_by_antiloop", out var s) ? s : 0;
            Assert.Equal(0, runs);
            Assert.Equal(0, skipped);

            // Sanity — no ClassifierTick deadline left armed when the fast-path transitions
            // straight to terminal.
            Assert.Empty(result.FinalState.Deadlines);
        }

        [Fact]
        public void FastPath_records_inline_classifier_outcome_on_state()
        {
            var harness = NewHarness();
            var signals = LoadFixture("whiteglove-inline-v1.jsonl");
            var result = harness.Replay("session-anon-0014-fp2", "tenant-anon-0014", signals);

            // Inline-computed verdict must be persisted on ClassifierOutcomes — same shape
            // as the slow-path verdict so Inspector + downstream consumers see no diff.
            Assert.Equal(HypothesisLevel.Confirmed, result.FinalState.ClassifierOutcomes.WhiteGloveSealing.Level);
            Assert.True(result.FinalState.ClassifierOutcomes.WhiteGloveSealing.Score
                >= WhiteGloveSealingClassifier.HighThreshold);
            // Verdict-input-hash must be set so any racing RunClassifier effect would be
            // anti-loop-skipped against the inline result.
            Assert.False(string.IsNullOrEmpty(result.FinalState.ClassifierOutcomes.WhiteGloveSealing.LastClassifierVerdictId));
        }
    }
}
