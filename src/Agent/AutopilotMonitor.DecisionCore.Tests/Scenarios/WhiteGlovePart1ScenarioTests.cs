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

            // The tick fired between the two WG signals — snapshot unchanged at that point,
            // so it was anti-loop-skipped. The SealingPattern run and the ShellCore run both
            // produced fresh verdicts.
            var runs = harness.ClassifierRunStats.TryGetValue("whiteglove-sealing:run", out var r) ? r : 0;
            var skipped = harness.ClassifierRunStats.TryGetValue("whiteglove-sealing:skipped_by_antiloop", out var s) ? s : 0;
            Assert.Equal(2, runs);
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
    }
}
