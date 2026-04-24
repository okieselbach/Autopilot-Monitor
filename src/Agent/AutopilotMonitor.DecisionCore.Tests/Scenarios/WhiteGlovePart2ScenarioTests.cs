using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Classifiers;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.DecisionCore.Tests.Harness;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests.Scenarios
{
    /// <summary>
    /// Plan §4 M3.4 — WhiteGlove Part 2 scenarios.
    /// </summary>
    public sealed class WhiteGlovePart2ScenarioTests : ScenarioTestBase
    {
        private static ClassifierAwareReplayHarness NewHarness() =>
            new ClassifierAwareReplayHarness(
                new DecisionEngine(),
                new Dictionary<string, IClassifier>
                {
                    [WhiteGloveSealingClassifier.ClassifierId] = new WhiteGloveSealingClassifier(),
                    [WhiteGlovePart2CompletionClassifier.ClassifierId] = new WhiteGlovePart2CompletionClassifier(),
                });

        [Fact]
        public void WhiteGlovePart2Happy_fourUserSignals_sealPart2()
        {
            var signals = LoadFixture("whiteglove-part2-happy-v1.jsonl");
            var result = NewHarness().Replay("session-anon-0020", "tenant-anon-0020", signals);

            Assert.Equal(SessionStage.WhiteGloveCompletedPart2, result.FinalState.Stage);
            Assert.Equal(SessionOutcome.WhiteGlovePart2Complete, result.FinalState.Outcome);
            Assert.Equal(HypothesisLevel.Confirmed, result.FinalState.ClassifierOutcomes.WhiteGlovePart2Completion.Level);
            Assert.Equal(100, result.FinalState.ClassifierOutcomes.WhiteGlovePart2Completion.Score);

            // All four Part-2 facts should be present.
            Assert.NotNull(result.FinalState.UserAadSignInCompleteUtc);
            Assert.NotNull(result.FinalState.HelloResolvedPart2Utc);
            Assert.NotNull(result.FinalState.DesktopArrivedPart2Utc);
            Assert.NotNull(result.FinalState.AccountSetupCompletedPart2Utc);

            // Part-1 hypothesis still Confirmed (not reset by the Part-2 transition).
            Assert.Equal(HypothesisLevel.Confirmed, result.FinalState.ClassifierOutcomes.WhiteGloveSealing.Level);

            // SystemReboot fact recorded from SessionRecovered.
            Assert.NotNull(result.FinalState.SystemRebootUtc);

            Assert.Empty(result.FinalState.Deadlines);
        }

        [Fact]
        public void WhiteGlovePart2Stuck_safetyDeadlineFires_failsSession()
        {
            var signals = LoadFixture("whiteglove-part2-stuck-v1.jsonl");
            var result = NewHarness().Replay("session-anon-0021", "tenant-anon-0021", signals);

            Assert.Equal(SessionStage.Failed, result.FinalState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentFailed, result.FinalState.Outcome);
            Assert.Equal(HypothesisLevel.Rejected, result.FinalState.ClassifierOutcomes.WhiteGlovePart2Completion.Level);
            Assert.Equal("part2_user_absent", result.FinalState.ClassifierOutcomes.WhiteGlovePart2Completion.Reason);

            // No user-signal facts captured.
            Assert.Null(result.FinalState.UserAadSignInCompleteUtc);
            Assert.Null(result.FinalState.HelloResolvedPart2Utc);
            Assert.Null(result.FinalState.DesktopArrivedPart2Utc);
            Assert.Null(result.FinalState.AccountSetupCompletedPart2Utc);

            // Part 1 verdict remains Confirmed even as Part 2 failed.
            Assert.Equal(HypothesisLevel.Confirmed, result.FinalState.ClassifierOutcomes.WhiteGloveSealing.Level);
            Assert.Empty(result.FinalState.Deadlines);
        }

        [Fact]
        public void WhiteGlovePart2_bridge_armsSafetyDeadline_onRecoverFromSealed()
        {
            // Intermediate-state check by running a shortened fixture: after Part-1 seals and
            // SessionRecovered fires, the safety deadline should be active. The happy path
            // then cancels it when the classifier reaches Confirmed; here we just verify the
            // bridge behaviour by loading only the first 4 signals of the happy fixture.
            var full = LoadFixture("whiteglove-part2-happy-v1.jsonl");
            var prefix = new List<AutopilotMonitor.DecisionCore.Signals.DecisionSignal>();
            for (int i = 0; i < 4 && i < full.Count; i++) prefix.Add(full[i]);

            var result = NewHarness().Replay("session-anon-0022", "tenant-anon-0022", prefix);

            Assert.Equal(SessionStage.WhiteGloveAwaitingUserSignIn, result.FinalState.Stage);
            Assert.Contains(
                result.FinalState.Deadlines,
                d => d.Name == DeadlineNames.WhiteGlovePart2Safety);
        }
    }
}
