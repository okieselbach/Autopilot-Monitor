using System.Linq;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.State;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests.Scenarios
{
    /// <summary>
    /// Plan §4 M3.2 — SelfDeploying-v1 and Device-Only scenarios.
    /// </summary>
    public sealed class SelfDeployingScenarioTests : ScenarioTestBase
    {
        [Fact]
        public void SelfDeployingHappy_completes_onProvisioningComplete_noUserFacts()
        {
            var result = RunFixture(
                fixtureFilename: "selfdeploying-happy-v1.jsonl",
                sessionId: "session-anon-0004",
                tenantId: "tenant-anon-0004");

            Assert.Equal(SessionStage.Completed, result.FinalState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, result.FinalState.Outcome);
            Assert.Empty(result.FinalState.Deadlines);

            // No user-presence facts should have been produced.
            Assert.Null(result.FinalState.HelloResolvedUtc);
            Assert.Null(result.FinalState.DesktopArrivedUtc);
            Assert.Null(result.FinalState.ScenarioObservations.AadUserJoinWithUserObserved);
            Assert.Null(result.FinalState.AccountSetupEnteredUtc);

            // DeviceOnly hypothesis confirmed as DeviceOnly (no user presence seen).
            Assert.Equal(HypothesisLevel.Confirmed, result.FinalState.ClassifierOutcomes.DeviceOnlyDeployment.Level);
            Assert.Equal(
                DecisionEngine.DeviceOnlyReasons.DeviceOnly,
                result.FinalState.ClassifierOutcomes.DeviceOnlyDeployment.Reason);

            // Profile upgraded to Mode=SelfDeploying @ High by the SelfDeploying handler.
            Assert.Equal(EnrollmentMode.SelfDeploying, result.FinalState.ScenarioProfile.Mode);
            Assert.Equal(ProfileConfidence.High, result.FinalState.ScenarioProfile.Confidence);
            Assert.Equal("selfdeploying_provisioning_complete", result.FinalState.ScenarioProfile.Reason);

            Assert.Equal(3, result.Transitions.Count);
            Assert.All(result.Transitions, t => Assert.True(t.Taken));
        }

        [Fact]
        public void SelfDeployingHappy_schedulesAndCancelsDeviceOnlyEspDetection()
        {
            var result = RunFixture(
                fixtureFilename: "selfdeploying-happy-v1.jsonl",
                sessionId: "session-anon-0004",
                tenantId: "tenant-anon-0004");

            // Replay intermediate check via harness is not exposed, but we can assert the
            // final state has no active deadlines (the provisioning-complete handler clears them).
            Assert.Empty(result.FinalState.Deadlines);
        }

        [Fact]
        public void DeviceOnlyEspExitUnknown_deadlineFires_then_provisioningCompletes()
        {
            var result = RunFixture(
                fixtureFilename: "selfdeploying-esp-exit-unknown-v1.jsonl",
                sessionId: "session-anon-0005",
                tenantId: "tenant-anon-0005");

            Assert.Equal(SessionStage.Completed, result.FinalState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, result.FinalState.Outcome);

            // DeviceOnlyDeployment goes through Strong (by deadline) -> Confirmed (by provisioning).
            Assert.Equal(HypothesisLevel.Confirmed, result.FinalState.ClassifierOutcomes.DeviceOnlyDeployment.Level);
            Assert.Equal(
                DecisionEngine.DeviceOnlyReasons.DeviceOnly,
                result.FinalState.ClassifierOutcomes.DeviceOnlyDeployment.Reason);

            // The deadline transition is present and taken (hypothesis update without stage change).
            var deadlineTransition = Assert.Single(
                result.Transitions,
                t => t.Trigger == $"DeadlineFired:{DeadlineNames.DeviceOnlyEspDetection}");
            Assert.True(deadlineTransition.Taken);
            Assert.Equal(deadlineTransition.FromStage, deadlineTransition.ToStage);
            Assert.Equal(SessionStage.EspDeviceSetup, deadlineTransition.ToStage);

            Assert.Equal(4, result.Transitions.Count);
        }

        [Fact]
        public void Classic_AccountSetupPhase_CancelsDeviceOnlyEspDetection()
        {
            // Re-use the UserDriven-Happy fixture (already in M3.1): it goes
            // DeviceSetup -> AccountSetup, so the DeviceOnlyEspDetection deadline is
            // scheduled and then cancelled before it can fire. The final state must
            // carry no DeviceOnlyEspDetection deadline, and the DeviceOnly hypothesis
            // stays Unknown — the UserDriven path does not classify itself as DeviceOnly.
            var result = RunFixture(
                fixtureFilename: "userdriven-happy-v1.jsonl",
                sessionId: "s", tenantId: "t");

            Assert.DoesNotContain(
                result.FinalState.Deadlines,
                d => d.Name == DeadlineNames.DeviceOnlyEspDetection);
            Assert.Equal(HypothesisLevel.Unknown, result.FinalState.ClassifierOutcomes.DeviceOnlyDeployment.Level);
        }
    }
}
