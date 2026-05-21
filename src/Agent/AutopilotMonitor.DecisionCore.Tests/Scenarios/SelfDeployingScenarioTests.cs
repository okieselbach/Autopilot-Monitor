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

            // Plan v9 — terminal classification now happens at DeadlineFired, not at signal-time.
            // Reason changed from "selfdeploying_provisioning_complete" to "selfdeploying_deadline_confirmed".
            Assert.Equal(EnrollmentMode.SelfDeploying, result.FinalState.ScenarioProfile.Mode);
            Assert.Equal(ProfileConfidence.High, result.FinalState.ScenarioProfile.Confidence);
            Assert.Equal("selfdeploying_deadline_confirmed", result.FinalState.ScenarioProfile.Reason);

            // Fixture now drives 4 signals (SessionStarted, EspPhaseChanged(DeviceSetup),
            // DeviceSetupProvisioningComplete, DeadlineFired) instead of 3.
            Assert.Equal(4, result.Transitions.Count);
            Assert.All(result.Transitions, t => Assert.True(t.Taken));
        }

        [Fact]
        public void SelfDeployingHappy_armsAndClearsDeviceOnlyEspDetectionViaDeadline()
        {
            // Plan v9 (88a53223 defang) — semantics:
            // - DeviceSetupProvisioningComplete arms the deadline (no longer terminal at signal-time).
            // - DeadlineFired completes the session (after all guards pass) and clears deadlines.
            // Final state still has no active deadlines, just via a different mechanism.
            var result = RunFixture(
                fixtureFilename: "selfdeploying-happy-v1.jsonl",
                sessionId: "session-anon-0004",
                tenantId: "tenant-anon-0004");

            Assert.Empty(result.FinalState.Deadlines);
            // Anchor must be set so downstream stale-fire guards can distinguish "new path" from
            // "rollout-race deadline from old code".
            Assert.NotNull(result.FinalState.DeviceSetupResolvedUtc);
        }

        [Fact]
        public void DeviceOnlyEspExitUnknown_provisioningCompletesArmsDeadline_thenFiresToTerminal()
        {
            // Plan v9 semantics: DeviceSetupProvisioningComplete arms the deadline; DeadlineFired
            // is the sole SelfDeploying-terminal entry. The same fixture (now without the legacy
            // arm-at-DeviceSetup-start DeadlineFired) drives the same final state via the new path.
            var result = RunFixture(
                fixtureFilename: "selfdeploying-esp-exit-unknown-v1.jsonl",
                sessionId: "session-anon-0005",
                tenantId: "tenant-anon-0005");

            Assert.Equal(SessionStage.Completed, result.FinalState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, result.FinalState.Outcome);

            // DeviceOnlyDeployment is now set to Confirmed exclusively in the deadline-fired terminal
            // branch (previously the v1 path set it twice — first at signal-time, then again at
            // deadline-fire). Final state is identical: Confirmed/DeviceOnly.
            Assert.Equal(HypothesisLevel.Confirmed, result.FinalState.ClassifierOutcomes.DeviceOnlyDeployment.Level);
            Assert.Equal(
                DecisionEngine.DeviceOnlyReasons.DeviceOnly,
                result.FinalState.ClassifierOutcomes.DeviceOnlyDeployment.Reason);

            // The DeadlineFired transition is the terminal one (Stage transitions to Completed
            // here, NOT a "hypothesis-only" no-op like in the v1 semantics).
            var deadlineTransition = Assert.Single(
                result.Transitions,
                t => t.Trigger == $"DeadlineFired:{DeadlineNames.DeviceOnlyEspDetection}");
            Assert.True(deadlineTransition.Taken);
            Assert.Equal(SessionStage.Completed, deadlineTransition.ToStage);

            // Fixture: SessionStarted + EspPhaseChanged + DeviceSetupProvisioningComplete + DeadlineFired = 4.
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
