using System;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests.Scenarios
{
    /// <summary>
    /// Plan §4 M3.1 — Classic UserDriven-v1 scenarios.
    /// Each test loads a committed, anonymized DecisionSignal JSONL fixture and asserts
    /// on the reducer's terminal state + key hypothesis / fact outcomes.
    /// </summary>
    public sealed class ClassicScenarioTests : ScenarioTestBase
    {
        [Fact]
        public void UserDrivenHappy_reaches_Completed_withEnrollmentComplete()
        {
            var result = RunFixture(
                fixtureFilename: "userdriven-happy-v1.jsonl",
                sessionId: "session-anon-0001",
                tenantId: "tenant-anon-0001");

            Assert.Equal(SessionStage.Completed, result.FinalState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, result.FinalState.Outcome);
            Assert.Equal("Success", result.FinalState.HelloOutcome!.Value);
            Assert.NotNull(result.FinalState.HelloResolvedUtc);
            Assert.NotNull(result.FinalState.DesktopArrivedUtc);
            Assert.NotNull(result.FinalState.EspFinalExitUtc);
            Assert.Equal(EnrollmentPhase.AccountSetup, result.FinalState.CurrentEnrollmentPhase!.Value);
            Assert.Empty(result.FinalState.Deadlines);

            // EnrollmentType should reach Strong after ImeUserSessionCompleted.
            Assert.Equal(HypothesisLevel.Strong, result.FinalState.EnrollmentType.Level);
            Assert.Equal("ime_user_session_completed", result.FinalState.EnrollmentType.Reason);

            // All 7 signals produced taken transitions.
            Assert.Equal(7, result.Transitions.Count);
            Assert.All(result.Transitions, t => Assert.True(t.Taken));

            // A completion effect should have been emitted at the last step.
            var last = result.Transitions[^1];
            Assert.Equal("DesktopArrived", last.Trigger);
        }

        [Fact]
        public void UserDrivenHelloTimeout_completes_withHelloOutcomeTimeout()
        {
            var result = RunFixture(
                fixtureFilename: "userdriven-hello-timeout-v1.jsonl",
                sessionId: "session-anon-0002",
                tenantId: "tenant-anon-0002");

            Assert.Equal(SessionStage.Completed, result.FinalState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, result.FinalState.Outcome);
            Assert.Equal("Timeout", result.FinalState.HelloOutcome!.Value);
            Assert.NotNull(result.FinalState.HelloResolvedUtc);
            Assert.NotNull(result.FinalState.DesktopArrivedUtc);
            Assert.Empty(result.FinalState.Deadlines);

            // The terminating signal is DeadlineFired:hello_safety.
            Assert.EndsWith("hello_safety", result.Transitions[^1].Trigger);
            Assert.True(result.Transitions[^1].Taken);
        }

        [Fact]
        public void LateAadj_neverCompletesPrematurely_hypothesisAnnotated()
        {
            var result = RunFixture(
                fixtureFilename: "late-aadj-v1.jsonl",
                sessionId: "session-anon-0003",
                tenantId: "tenant-anon-0003");

            // Session still completes normally — via Hello + Desktop, NOT via late-AADJ.
            Assert.Equal(SessionStage.Completed, result.FinalState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, result.FinalState.Outcome);
            Assert.Equal("Success", result.FinalState.HelloOutcome!.Value);

            // AadJoinedWithUser fact is recorded from the Late-AADJ signal.
            Assert.NotNull(result.FinalState.AadJoinedWithUser);
            Assert.True(result.FinalState.AadJoinedWithUser!.Value);

            // EnrollmentType reason carries the late-AADJ annotation (set by the handler),
            // and then advances to "ime_user_session_completed" on the IME signal.
            Assert.Equal("ime_user_session_completed", result.FinalState.EnrollmentType.Reason);

            // Hypothesis chain:
            //   1. EspPhaseChanged(AccountSetup) -> Weak / account_setup_observed
            //   2. AadUserJoinedLate -> Weak / late_aadj_observed (stage unchanged)
            //   3. ImeUserSessionCompleted -> Strong / ime_user_session_completed
            Assert.Equal(HypothesisLevel.Strong, result.FinalState.EnrollmentType.Level);

            // Critical regression guard: the AadUserJoinedLate transition did NOT take the
            // session to a terminal stage (stayed on EspAccountSetup). It is taken=true (the
            // fact was applied) but FromStage==ToStage.
            var aadTransition = Assert.Single(result.Transitions, t => t.Trigger == "AadUserJoinedLate");
            Assert.True(aadTransition.Taken);
            Assert.Equal(aadTransition.FromStage, aadTransition.ToStage);
            Assert.Equal(SessionStage.EspAccountSetup, aadTransition.ToStage);
        }

        [Fact]
        public void UserDrivenHappy_replayIsDeterministic_sameHashAcrossRuns()
        {
            var r1 = RunFixture("userdriven-happy-v1.jsonl", "s", "t");
            var r2 = RunFixture("userdriven-happy-v1.jsonl", "s", "t");
            Assert.Equal(r1.FinalStepHash, r2.FinalStepHash);
            Assert.Equal(16, r1.FinalStepHash.Length);
        }
    }
}
