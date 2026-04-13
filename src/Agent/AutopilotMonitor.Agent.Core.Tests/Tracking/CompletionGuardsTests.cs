using System;
using AutopilotMonitor.Agent.Core.Monitoring.Tracking;
using Xunit;

namespace AutopilotMonitor.Agent.Core.Tests.Tracking
{
    public class CompletionGuardsTests
    {
        // ===== Hello Resolution Guard =====

        [Fact]
        public void HelloGuard_NoTracker_ReturnsResolved()
        {
            Assert.True(CompletionGuards.IsHelloResolved(
                hasTracker: false, isHelloCompleted: false,
                isPolicyConfigured: false, isDeviceOnly: false));
        }

        [Fact]
        public void HelloGuard_HelloCompleted_ReturnsResolved()
        {
            Assert.True(CompletionGuards.IsHelloResolved(
                hasTracker: true, isHelloCompleted: true,
                isPolicyConfigured: true, isDeviceOnly: false));
        }

        [Fact]
        public void HelloGuard_PolicyNotConfigured_ReturnsResolved()
        {
            Assert.True(CompletionGuards.IsHelloResolved(
                hasTracker: true, isHelloCompleted: false,
                isPolicyConfigured: false, isDeviceOnly: false));
        }

        [Fact]
        public void HelloGuard_DeviceOnly_ReturnsResolved()
        {
            Assert.True(CompletionGuards.IsHelloResolved(
                hasTracker: true, isHelloCompleted: false,
                isPolicyConfigured: true, isDeviceOnly: true));
        }

        [Fact]
        public void HelloGuard_PolicyConfigured_HelloPending_ReturnsPending()
        {
            Assert.False(CompletionGuards.IsHelloResolved(
                hasTracker: true, isHelloCompleted: false,
                isPolicyConfigured: true, isDeviceOnly: false));
        }

        // ===== ESP Gate Guard =====

        [Fact]
        public void EspGate_DesktopArrival_EspActive_V1_Blocks()
        {
            Assert.True(CompletionGuards.IsEspGateBlocking(
                source: "desktop_arrival", enrollmentType: "v1",
                espEverSeen: true, espFinalExitSeen: false));
        }

        [Fact]
        public void EspGate_DesktopHello_EspActive_V1_Blocks()
        {
            Assert.True(CompletionGuards.IsEspGateBlocking(
                source: "desktop_hello", enrollmentType: "v1",
                espEverSeen: true, espFinalExitSeen: false));
        }

        [Fact]
        public void EspGate_DesktopArrival_EspExited_Passes()
        {
            Assert.False(CompletionGuards.IsEspGateBlocking(
                source: "desktop_arrival", enrollmentType: "v1",
                espEverSeen: true, espFinalExitSeen: true));
        }

        [Fact]
        public void EspGate_DesktopArrival_V2_Passes()
        {
            Assert.False(CompletionGuards.IsEspGateBlocking(
                source: "desktop_arrival", enrollmentType: "v2",
                espEverSeen: true, espFinalExitSeen: false));
        }

        [Fact]
        public void EspGate_NonDesktopSource_Passes()
        {
            Assert.False(CompletionGuards.IsEspGateBlocking(
                source: "esp_hello_composite", enrollmentType: "v1",
                espEverSeen: true, espFinalExitSeen: false));
        }

        [Fact]
        public void EspGate_EspNeverSeen_Passes()
        {
            Assert.False(CompletionGuards.IsEspGateBlocking(
                source: "desktop_arrival", enrollmentType: "v1",
                espEverSeen: false, espFinalExitSeen: false));
        }

        // ===== Hybrid Reboot Gate Guard =====

        [Fact]
        public void HybridRebootGate_NonHybrid_Passes()
        {
            Assert.False(CompletionGuards.IsHybridRebootGateBlocking(
                isHybridJoin: false, source: "esp_hello_composite",
                imePatternSeenUtc: null, espFinalExitUtc: DateTime.UtcNow.AddMinutes(-5),
                agentStartTimeUtc: DateTime.UtcNow.AddMinutes(-10)));
        }

        [Fact]
        public void HybridRebootGate_NonCompositeSource_Passes()
        {
            Assert.False(CompletionGuards.IsHybridRebootGateBlocking(
                isHybridJoin: true, source: "ime_pattern",
                imePatternSeenUtc: null, espFinalExitUtc: DateTime.UtcNow.AddMinutes(-5),
                agentStartTimeUtc: DateTime.UtcNow.AddMinutes(-10)));
        }

        [Fact]
        public void HybridRebootGate_Hybrid_NoIme_NoRestart_Blocks()
        {
            var espExitTime = DateTime.UtcNow.AddMinutes(-5);
            var agentStartBeforeEspExit = espExitTime.AddMinutes(-10);

            Assert.True(CompletionGuards.IsHybridRebootGateBlocking(
                isHybridJoin: true, source: "esp_hello_composite",
                imePatternSeenUtc: null, espFinalExitUtc: espExitTime,
                agentStartTimeUtc: agentStartBeforeEspExit));
        }

        [Fact]
        public void HybridRebootGate_Hybrid_ImeCompleted_Passes()
        {
            var espExitTime = DateTime.UtcNow.AddMinutes(-5);
            var agentStartBeforeEspExit = espExitTime.AddMinutes(-10);

            Assert.False(CompletionGuards.IsHybridRebootGateBlocking(
                isHybridJoin: true, source: "esp_hello_composite",
                imePatternSeenUtc: DateTime.UtcNow.AddMinutes(-1),
                espFinalExitUtc: espExitTime,
                agentStartTimeUtc: agentStartBeforeEspExit));
        }

        [Fact]
        public void HybridRebootGate_Hybrid_AgentRestartedAfterEspExit_Passes()
        {
            var espExitTime = DateTime.UtcNow.AddMinutes(-10);
            var agentStartAfterEspExit = espExitTime.AddMinutes(5);

            Assert.False(CompletionGuards.IsHybridRebootGateBlocking(
                isHybridJoin: true, source: "esp_hello_composite",
                imePatternSeenUtc: null, espFinalExitUtc: espExitTime,
                agentStartTimeUtc: agentStartAfterEspExit));
        }

        [Fact]
        public void HybridRebootGate_Hybrid_EspExitUtcNull_Blocks()
        {
            // Edge case: hybrid join, composite source, no ESP exit recorded
            // agentRestartedAfterEspExit = false (espFinalExitUtc is null)
            Assert.True(CompletionGuards.IsHybridRebootGateBlocking(
                isHybridJoin: true, source: "esp_hello_composite",
                imePatternSeenUtc: null, espFinalExitUtc: null,
                agentStartTimeUtc: DateTime.UtcNow));
        }

        // ===== Device-Only Deployment Classification =====

        [Fact]
        public void IsDeviceOnly_SelfDeploying_True()
        {
            Assert.True(CompletionGuards.IsDeviceOnlyDeployment(
                autopilotMode: 1, skipUserStatusPage: null, aadJoinedWithUser: false));
        }

        [Fact]
        public void IsDeviceOnly_SkipUserNoAadUser_True()
        {
            Assert.True(CompletionGuards.IsDeviceOnlyDeployment(
                autopilotMode: 0, skipUserStatusPage: true, aadJoinedWithUser: false));
        }

        [Fact]
        public void IsDeviceOnly_SkipUserWithAadUser_False()
        {
            Assert.False(CompletionGuards.IsDeviceOnlyDeployment(
                autopilotMode: 0, skipUserStatusPage: true, aadJoinedWithUser: true));
        }

        [Fact]
        public void IsDeviceOnly_UserDriven_NoSkip_False()
        {
            Assert.False(CompletionGuards.IsDeviceOnlyDeployment(
                autopilotMode: 0, skipUserStatusPage: false, aadJoinedWithUser: false));
        }

        [Fact]
        public void IsDeviceOnly_UnknownMode_SkipUserNull_False()
        {
            Assert.False(CompletionGuards.IsDeviceOnlyDeployment(
                autopilotMode: null, skipUserStatusPage: null, aadJoinedWithUser: false));
        }
    }
}
