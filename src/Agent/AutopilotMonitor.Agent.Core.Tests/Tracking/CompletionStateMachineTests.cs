using System;
using System.Collections.Generic;
using Xunit;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Flows;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Completion;

namespace AutopilotMonitor.Agent.Core.Tests.Tracking
{
    public class CompletionStateMachineTests
    {
        // ===== Helper Methods =====

        private static CompletionContext DefaultContext(
            string enrollmentType = "v1",
            bool isHybridJoin = false,
            int? autopilotMode = 0,
            bool? skipUserStatusPage = false,
            bool aadJoinedWithUser = false,
            bool hasHelloTracker = true,
            bool isHelloCompleted = false,
            bool isHelloPolicyConfigured = true,
            bool hasUnresolvedEspCategories = false,
            bool deviceInfoCollected = true,
            string lastEspPhase = null,
            string source = null,
            bool whiteGloveStartDetected = false,
            bool hasSaveWhiteGloveSuccessResult = false)
        {
            return new CompletionContext
            {
                EnrollmentType = enrollmentType,
                IsHybridJoin = isHybridJoin,
                AutopilotMode = autopilotMode,
                SkipUserStatusPage = skipUserStatusPage,
                AadJoinedWithUser = aadJoinedWithUser,
                HasHelloTracker = hasHelloTracker,
                IsHelloCompleted = isHelloCompleted,
                IsHelloPolicyConfigured = isHelloPolicyConfigured,
                HasUnresolvedEspCategories = hasUnresolvedEspCategories,
                DeviceInfoCollected = deviceInfoCollected,
                LastEspPhase = lastEspPhase,
                Source = source,
                WhiteGloveStartDetected = whiteGloveStartDetected,
                HasSaveWhiteGloveSuccessResult = hasSaveWhiteGloveSuccessResult,
                AgentStartTimeUtc = DateTime.UtcNow.AddMinutes(-30),
                EspFinalExitUtc = null,
                EspEverSeen = false,
                EspFinalExitSeen = false,
                DesktopArrived = false,
                ImePatternSeenUtc = null
            };
        }

        private static CompletionContext DeviceOnlyContext(int? autopilotMode = 1)
        {
            return DefaultContext(
                autopilotMode: autopilotMode,
                skipUserStatusPage: autopilotMode == 1 ? (bool?)null : true,
                aadJoinedWithUser: false,
                isHelloPolicyConfigured: false);
        }

        private static CompletionContext HybridJoinContext()
        {
            return DefaultContext(
                isHybridJoin: true,
                lastEspPhase: "AccountSetup");
        }

        // ===== SECTION 1: State Transition Unit Tests =====

        [Fact]
        public void Idle_EspPhaseChanged_TransitionsToEspActive()
        {
            var sm = new CompletionStateMachine();
            var result = sm.ProcessTrigger("esp_phase_changed", DefaultContext());

            Assert.True(result.Transitioned);
            Assert.Equal(EnrollmentCompletionState.EspActive, sm.CurrentState);
        }

        [Fact]
        public void Idle_DesktopArrived_NoEsp_TransitionsToDesktopArrivedAwaitingHello()
        {
            var sm = new CompletionStateMachine();
            var ctx = DefaultContext(isHelloPolicyConfigured: true, isHelloCompleted: false);
            var result = sm.ProcessTrigger("desktop_arrived", ctx);

            Assert.True(result.Transitioned);
            Assert.Equal(EnrollmentCompletionState.DesktopArrivedAwaitingHello, sm.CurrentState);
            Assert.True(result.ShouldStartHelloWaitTimer);
        }

        [Fact]
        public void Idle_DesktopArrived_NoHelloTracker_CompletesImmediately()
        {
            var sm = new CompletionStateMachine();
            var ctx = DefaultContext(hasHelloTracker: false);
            var result = sm.ProcessTrigger("desktop_arrived", ctx);

            Assert.True(result.Transitioned);
            Assert.Equal(EnrollmentCompletionState.Completed, sm.CurrentState);
            Assert.True(result.ShouldEmitEnrollmentComplete);
            Assert.Equal("desktop_arrival", result.CompletionSource);
        }

        [Fact]
        public void EspActive_EspExitingFromAccountSetup_TransitionsToEspExited()
        {
            var sm = new CompletionStateMachine();
            sm.ProcessTrigger("esp_phase_changed", DefaultContext());

            var ctx = DefaultContext(lastEspPhase: "AccountSetup");
            var result = sm.ProcessTrigger("esp_exiting", ctx);

            Assert.True(result.Transitioned);
            Assert.True(result.ShouldStartHelloWaitTimer);
            Assert.True(result.ShouldPersistState);
            Assert.Contains("esp_final_exit", result.SignalsToRecord);
        }

        [Fact]
        public void EspActive_EspExitingSkipUser_TransitionsToDeviceOnly()
        {
            var sm = new CompletionStateMachine();
            sm.ProcessTrigger("esp_phase_changed", DefaultContext());

            var ctx = DefaultContext(
                lastEspPhase: "DeviceSetup",
                skipUserStatusPage: true,
                aadJoinedWithUser: false,
                isHelloPolicyConfigured: false);
            var result = sm.ProcessTrigger("esp_exiting", ctx);

            Assert.True(result.Transitioned);
            Assert.Contains("device_only_esp_registry", result.SignalsToRecord);
        }

        [Fact]
        public void EspActive_EspExitingFallback_StartsDeviceOnlyTimer()
        {
            var sm = new CompletionStateMachine();
            sm.ProcessTrigger("esp_phase_changed", DefaultContext());

            var ctx = DefaultContext(lastEspPhase: "DeviceSetup", skipUserStatusPage: null);
            var result = sm.ProcessTrigger("esp_exiting", ctx);

            Assert.True(result.ShouldStartDeviceOnlyEspTimer);
            Assert.Equal(EnrollmentCompletionState.EspActive, sm.CurrentState);
        }

        [Fact]
        public void EspActive_DesktopArrived_TransitionsToDesktopArrivedEspBlocking()
        {
            var sm = new CompletionStateMachine();
            sm.ProcessTrigger("esp_phase_changed", DefaultContext());

            var ctx = DefaultContext(skipUserStatusPage: false);
            var result = sm.ProcessTrigger("desktop_arrived", ctx);

            Assert.True(result.Transitioned);
            Assert.Equal(EnrollmentCompletionState.DesktopArrivedEspBlocking, sm.CurrentState);
        }

        [Fact]
        public void EspExited_HelloCompleted_TransitionsToCompleted()
        {
            var sm = new CompletionStateMachine();
            // Set up: ESP seen -> ESP exited
            sm.ProcessTrigger("esp_phase_changed", DefaultContext());
            sm.ProcessTrigger("esp_exiting", DefaultContext(lastEspPhase: "AccountSetup"));

            var ctx = DefaultContext(isHelloCompleted: true);
            var result = sm.ProcessTrigger("hello_completed", ctx);

            Assert.Equal(EnrollmentCompletionState.Completed, sm.CurrentState);
            Assert.True(result.ShouldEmitEnrollmentComplete);
            Assert.Equal("esp_hello_composite", result.CompletionSource);
        }

        [Fact]
        public void EspExited_HelloCompleted_HybridGate_TransitionsToBlocked()
        {
            var sm = new CompletionStateMachine();
            sm.ProcessTrigger("esp_phase_changed", DefaultContext());

            // ESP exits — but hybrid join with agent started before ESP exit
            var espExitTime = DateTime.UtcNow;
            var ctx = HybridJoinContext();
            ctx.EspFinalExitUtc = espExitTime;
            ctx.AgentStartTimeUtc = espExitTime.AddMinutes(-10); // Started BEFORE ESP exit
            sm.ProcessTrigger("esp_exiting", ctx);

            // Hello completes — but reboot gate should block
            var helloCtx = HybridJoinContext();
            helloCtx.IsHelloCompleted = true;
            helloCtx.EspFinalExitUtc = espExitTime;
            helloCtx.AgentStartTimeUtc = espExitTime.AddMinutes(-10);
            helloCtx.ImePatternSeenUtc = null; // No IME completion
            var result = sm.ProcessTrigger("hello_completed", helloCtx);

            Assert.Equal(EnrollmentCompletionState.HybridRebootGateBlocked, sm.CurrentState);
            Assert.False(result.ShouldEmitEnrollmentComplete);
        }

        [Fact]
        public void HybridGateBlocked_ImePattern_TransitionsToCompleted()
        {
            var sm = new CompletionStateMachine();
            sm.ProcessTrigger("esp_phase_changed", DefaultContext());

            var espExitTime = DateTime.UtcNow;
            var ctx = HybridJoinContext();
            ctx.EspFinalExitUtc = espExitTime;
            ctx.AgentStartTimeUtc = espExitTime.AddMinutes(-10);
            sm.ProcessTrigger("esp_exiting", ctx);

            var helloCtx = HybridJoinContext();
            helloCtx.IsHelloCompleted = true;
            helloCtx.EspFinalExitUtc = espExitTime;
            helloCtx.AgentStartTimeUtc = espExitTime.AddMinutes(-10);
            sm.ProcessTrigger("hello_completed", helloCtx);
            Assert.Equal(EnrollmentCompletionState.HybridRebootGateBlocked, sm.CurrentState);

            // IME pattern arrives — should unlock
            var imeCtx = HybridJoinContext();
            imeCtx.IsHelloCompleted = true;
            imeCtx.ImePatternSeenUtc = DateTime.UtcNow;
            imeCtx.IsHelloPolicyConfigured = false; // Hello already resolved
            var result = sm.ProcessTrigger("ime_user_session_completed", imeCtx);

            Assert.Equal(EnrollmentCompletionState.Completed, sm.CurrentState);
            Assert.True(result.ShouldEmitEnrollmentComplete);
        }

        [Fact]
        public void WaitingForHello_HelloCompleted_TransitionsToCompleted()
        {
            var sm = new CompletionStateMachine();
            sm.RestoreState(EnrollmentCompletionState.WaitingForHello,
                espEverSeen: true, espFinalExitSeen: true, desktopArrived: false,
                isWaitingForHello: true, isWaitingForEspSettle: false, deferredSource: null);

            var ctx = DefaultContext(isHelloCompleted: true);
            var result = sm.ProcessTrigger("hello_completed", ctx);

            Assert.Equal(EnrollmentCompletionState.Completed, sm.CurrentState);
            Assert.True(result.ShouldEmitEnrollmentComplete);
            Assert.Equal("ime_hello", result.CompletionSource);
        }

        [Fact]
        public void WaitingForHello_SafetyTimeout_TransitionsToCompleted()
        {
            var sm = new CompletionStateMachine();
            sm.RestoreState(EnrollmentCompletionState.WaitingForHello,
                espEverSeen: true, espFinalExitSeen: true, desktopArrived: false,
                isWaitingForHello: true, isWaitingForEspSettle: false, deferredSource: null);

            var result = sm.ProcessTrigger("hello_safety_timeout", DefaultContext());

            Assert.Equal(EnrollmentCompletionState.Completed, sm.CurrentState);
            Assert.True(result.ShouldEmitEnrollmentComplete);
            Assert.True(result.ShouldForceMarkHelloCompleted);
            Assert.Equal("ime_hello_safety_timeout", result.CompletionSource);
        }

        [Fact]
        public void WaitingForEspSettle_SettleTimeout_TransitionsToCompleted()
        {
            var sm = new CompletionStateMachine();
            sm.RestoreState(EnrollmentCompletionState.WaitingForEspSettle,
                espEverSeen: true, espFinalExitSeen: true, desktopArrived: false,
                isWaitingForHello: false, isWaitingForEspSettle: true, deferredSource: null);

            var result = sm.ProcessTrigger("esp_settle_timeout", DefaultContext());

            Assert.Equal(EnrollmentCompletionState.Completed, sm.CurrentState);
            Assert.True(result.ShouldEmitEnrollmentComplete);
            Assert.Equal("ime_pattern", result.CompletionSource);
        }

        [Fact]
        public void DesktopEspBlocking_EspFinalExit_HelloResolved_TransitionsToCompleted()
        {
            var sm = new CompletionStateMachine();
            sm.ProcessTrigger("esp_phase_changed", DefaultContext());
            sm.ProcessTrigger("desktop_arrived", DefaultContext(skipUserStatusPage: false));
            Assert.Equal(EnrollmentCompletionState.DesktopArrivedEspBlocking, sm.CurrentState);

            // ESP exits — Hello already resolved
            var ctx = DefaultContext(lastEspPhase: "AccountSetup", isHelloCompleted: true);
            var result = sm.ProcessTrigger("esp_exiting", ctx);

            Assert.Equal(EnrollmentCompletionState.Completed, sm.CurrentState);
            Assert.True(result.ShouldEmitEnrollmentComplete);
        }

        [Fact]
        public void DesktopEspBlocking_EspFinalExit_HelloPending_TransitionsToAwaitingCompletion()
        {
            var sm = new CompletionStateMachine();
            sm.ProcessTrigger("esp_phase_changed", DefaultContext());
            sm.ProcessTrigger("desktop_arrived", DefaultContext(skipUserStatusPage: false));

            // ESP exits — Hello still pending
            var ctx = DefaultContext(lastEspPhase: "AccountSetup", isHelloCompleted: false);
            var result = sm.ProcessTrigger("esp_exiting", ctx);

            // Should be in EspExitedAwaitingCompletion (not Completed, Hello pending)
            Assert.NotEqual(EnrollmentCompletionState.Completed, sm.CurrentState);
            Assert.False(result.ShouldEmitEnrollmentComplete);
            Assert.True(result.ShouldStartHelloWaitTimer);
        }

        [Fact]
        public void DesktopAwaitingHello_HelloCompleted_TransitionsToCompleted()
        {
            var sm = new CompletionStateMachine();
            sm.RestoreState(EnrollmentCompletionState.DesktopArrivedAwaitingHello,
                espEverSeen: false, espFinalExitSeen: false, desktopArrived: true,
                isWaitingForHello: false, isWaitingForEspSettle: false, deferredSource: null);

            var ctx = DefaultContext(isHelloCompleted: true);
            var result = sm.ProcessTrigger("hello_completed", ctx);

            Assert.Equal(EnrollmentCompletionState.Completed, sm.CurrentState);
            Assert.True(result.ShouldEmitEnrollmentComplete);
            Assert.Equal("desktop_hello", result.CompletionSource);
        }

        [Fact]
        public void DeviceOnly_ProvisioningComplete_TransitionsToCompleted()
        {
            var sm = new CompletionStateMachine();
            var ctx = DeviceOnlyContext();
            ctx.DeviceInfoCollected = true;
            var result = sm.ProcessTrigger("device_setup_provisioning_complete", ctx);

            Assert.Equal(EnrollmentCompletionState.Completed, sm.CurrentState);
            Assert.True(result.ShouldEmitEnrollmentComplete);
            Assert.Equal("self_deploying_provisioning_complete", result.CompletionSource);
        }

        [Fact]
        public void DeviceOnlySafety_Timeout_TransitionsToCompleted()
        {
            var sm = new CompletionStateMachine();
            sm.RestoreState(EnrollmentCompletionState.DeviceOnlySafetyWait,
                espEverSeen: true, espFinalExitSeen: true, desktopArrived: false,
                isWaitingForHello: false, isWaitingForEspSettle: false, deferredSource: null);

            var result = sm.ProcessTrigger("device_only_safety_timeout", DefaultContext());

            Assert.Equal(EnrollmentCompletionState.Completed, sm.CurrentState);
            Assert.True(result.ShouldEmitEnrollmentComplete);
            Assert.True(result.ShouldForceMarkHelloCompleted);
        }

        [Fact]
        public void AnyNonTerminal_EspFailure_TransitionsToFailed()
        {
            var sm = new CompletionStateMachine();
            sm.ProcessTrigger("esp_phase_changed", DefaultContext());

            var ctx = DefaultContext(source: "ESPProgress_Failure");
            var result = sm.ProcessTrigger("esp_failure_terminal", ctx);

            Assert.Equal(EnrollmentCompletionState.Failed, sm.CurrentState);
            Assert.True(result.ShouldEmitEnrollmentFailed);
        }

        [Fact]
        public void AnyNonTerminal_WhiteGlove_TransitionsToWhiteGloveCompleted()
        {
            var sm = new CompletionStateMachine();
            sm.ProcessTrigger("esp_phase_changed", DefaultContext());

            var result = sm.ProcessTrigger("whiteglove_complete", DefaultContext());

            Assert.Equal(EnrollmentCompletionState.WhiteGloveCompleted, sm.CurrentState);
            Assert.True(result.ShouldEmitWhiteGloveComplete);
        }

        [Fact]
        public void Completed_AnyTrigger_RemainsCompleted()
        {
            var sm = new CompletionStateMachine();
            sm.RestoreState(EnrollmentCompletionState.Completed,
                espEverSeen: true, espFinalExitSeen: true, desktopArrived: true,
                isWaitingForHello: false, isWaitingForEspSettle: false, deferredSource: null);

            var result = sm.ProcessTrigger("hello_completed", DefaultContext());
            Assert.False(result.Transitioned);
            Assert.Equal(EnrollmentCompletionState.Completed, sm.CurrentState);

            result = sm.ProcessTrigger("esp_phase_changed", DefaultContext());
            Assert.False(result.Transitioned);

            result = sm.ProcessTrigger("desktop_arrived", DefaultContext());
            Assert.False(result.Transitioned);
        }

        [Fact]
        public void Failed_AnyTrigger_RemainsFailed()
        {
            var sm = new CompletionStateMachine();
            sm.RestoreState(EnrollmentCompletionState.Failed,
                espEverSeen: true, espFinalExitSeen: false, desktopArrived: false,
                isWaitingForHello: false, isWaitingForEspSettle: false, deferredSource: null);

            var result = sm.ProcessTrigger("hello_completed", DefaultContext());
            Assert.False(result.Transitioned);
            Assert.Equal(EnrollmentCompletionState.Failed, sm.CurrentState);
        }

        [Fact]
        public void EspExited_EspResumed_Hybrid_TransitionsToEspActive()
        {
            var sm = new CompletionStateMachine();
            sm.ProcessTrigger("esp_phase_changed", DefaultContext());
            sm.ProcessTrigger("esp_exiting", DefaultContext(lastEspPhase: "AccountSetup", isHybridJoin: true));

            var ctx = DefaultContext(isHybridJoin: true);
            var result = sm.ProcessTrigger("esp_resumed", ctx);

            Assert.Equal(EnrollmentCompletionState.EspActive, sm.CurrentState);
            Assert.True(result.ShouldResetEspForResumption);
            Assert.True(result.ShouldPersistState);
            Assert.False(sm.EspFinalExitSeen);
        }

        [Fact]
        public void EspResumed_NonHybrid_NoTransition()
        {
            var sm = new CompletionStateMachine();
            sm.ProcessTrigger("esp_phase_changed", DefaultContext());
            sm.ProcessTrigger("esp_exiting", DefaultContext(lastEspPhase: "AccountSetup"));

            var result = sm.ProcessTrigger("esp_resumed", DefaultContext(isHybridJoin: false));
            Assert.False(result.Transitioned);
        }

        [Fact]
        public void DeviceOnlyEspTimer_AccountSetupArrives_NoTransition()
        {
            var sm = new CompletionStateMachine();
            sm.ProcessTrigger("esp_phase_changed", DefaultContext());

            var ctx = DefaultContext(lastEspPhase: "AccountSetup");
            var result = sm.ProcessTrigger("device_only_esp_timer_expired", ctx);

            Assert.False(result.Transitioned);
        }

        [Fact]
        public void DeviceOnlyEspTimer_NoDesktop_NoTransition()
        {
            var sm = new CompletionStateMachine();
            sm.ProcessTrigger("esp_phase_changed", DefaultContext());

            var ctx = DefaultContext(lastEspPhase: "DeviceSetup");
            var result = sm.ProcessTrigger("device_only_esp_timer_expired", ctx);

            Assert.False(result.Transitioned);
        }

        [Fact]
        public void DeviceOnlyEspTimer_DesktopAvailable_TransitionsToAwaitingHello()
        {
            var sm = new CompletionStateMachine();
            sm.ProcessTrigger("esp_phase_changed", DefaultContext());
            sm.ProcessTrigger("desktop_arrived", DefaultContext(skipUserStatusPage: false));

            var ctx = DefaultContext(lastEspPhase: "DeviceSetup");
            var result = sm.ProcessTrigger("device_only_esp_timer_expired", ctx);

            Assert.True(result.Transitioned);
            Assert.True(result.ShouldStartHelloWaitTimer);
            Assert.Contains("device_only_esp_final_exit", result.SignalsToRecord);
        }

        // ===== SECTION 2: Full Path Integration Tests =====

        [Fact]
        public void Path_DeviceOnly_SelfDeploying_FullSequence()
        {
            var sm = new CompletionStateMachine();

            // 1. ESP phase detected
            sm.ProcessTrigger("esp_phase_changed", DeviceOnlyContext());
            Assert.Equal(EnrollmentCompletionState.EspActive, sm.CurrentState);

            // 2. Device setup provisioning completes
            var ctx = DeviceOnlyContext();
            ctx.DeviceInfoCollected = true;
            var result = sm.ProcessTrigger("device_setup_provisioning_complete", ctx);

            Assert.Equal(EnrollmentCompletionState.Completed, sm.CurrentState);
            Assert.True(result.ShouldEmitEnrollmentComplete);
            Assert.Equal("self_deploying_provisioning_complete", result.CompletionSource);
        }

        [Fact]
        public void Path_DeviceOnly_SkipUser_FullSequence()
        {
            var sm = new CompletionStateMachine();

            // 1. ESP phase detected
            var ctx = DefaultContext(autopilotMode: 0, skipUserStatusPage: true, aadJoinedWithUser: false,
                isHelloPolicyConfigured: false);
            sm.ProcessTrigger("esp_phase_changed", ctx);

            // 2. ESP exits — SkipUserStatusPage=true
            ctx.LastEspPhase = "DeviceSetup";
            var result = sm.ProcessTrigger("esp_exiting", ctx);

            Assert.True(result.ShouldEmitEnrollmentComplete || sm.CurrentState == EnrollmentCompletionState.DeviceOnlySafetyWait);
        }

        [Fact]
        public void Path_EspAccountSetup_HelloResolves_FullSequence()
        {
            var sm = new CompletionStateMachine();

            // 1. ESP DeviceSetup
            sm.ProcessTrigger("esp_phase_changed", DefaultContext());

            // 2. ESP exits from AccountSetup
            sm.ProcessTrigger("esp_exiting", DefaultContext(lastEspPhase: "AccountSetup"));

            // 3. Hello resolves
            var ctx = DefaultContext(isHelloCompleted: true);
            var result = sm.ProcessTrigger("hello_completed", ctx);

            Assert.Equal(EnrollmentCompletionState.Completed, sm.CurrentState);
            Assert.True(result.ShouldEmitEnrollmentComplete);
            Assert.Equal("esp_hello_composite", result.CompletionSource);
        }

        [Fact]
        public void Path_EspAccountSetup_HelloAlreadyResolved_FullSequence()
        {
            var sm = new CompletionStateMachine();

            // 1. ESP DeviceSetup
            sm.ProcessTrigger("esp_phase_changed", DefaultContext());

            // 2. ESP exits from AccountSetup — Hello already resolved (backfill)
            var ctx = DefaultContext(lastEspPhase: "AccountSetup", isHelloCompleted: true);
            var result = sm.ProcessTrigger("esp_exiting", ctx);

            Assert.Equal(EnrollmentCompletionState.Completed, sm.CurrentState);
            Assert.True(result.ShouldEmitEnrollmentComplete);
            Assert.Equal("esp_hello_composite", result.CompletionSource);
        }

        [Fact]
        public void Path_HybridJoin_RebootGate_ImeCompletes_FullSequence()
        {
            var sm = new CompletionStateMachine();
            var espExitTime = DateTime.UtcNow;
            var agentStartBeforeEsp = espExitTime.AddMinutes(-30);

            // 1. ESP DeviceSetup
            sm.ProcessTrigger("esp_phase_changed", DefaultContext());

            // 2. ESP exits (hybrid join, agent started before ESP exit)
            var ctx = HybridJoinContext();
            ctx.EspFinalExitUtc = espExitTime;
            ctx.AgentStartTimeUtc = agentStartBeforeEsp;
            sm.ProcessTrigger("esp_exiting", ctx);

            // 3. Hello resolves — blocked by reboot gate
            var helloCtx = HybridJoinContext();
            helloCtx.IsHelloCompleted = true;
            helloCtx.EspFinalExitUtc = espExitTime;
            helloCtx.AgentStartTimeUtc = agentStartBeforeEsp;
            sm.ProcessTrigger("hello_completed", helloCtx);
            Assert.Equal(EnrollmentCompletionState.HybridRebootGateBlocked, sm.CurrentState);

            // 4. IME user session completes — gate unlocks
            var imeCtx = HybridJoinContext();
            imeCtx.IsHelloCompleted = true;
            imeCtx.ImePatternSeenUtc = DateTime.UtcNow;
            imeCtx.IsHelloPolicyConfigured = false;
            var result = sm.ProcessTrigger("ime_user_session_completed", imeCtx);

            Assert.Equal(EnrollmentCompletionState.Completed, sm.CurrentState);
            Assert.True(result.ShouldEmitEnrollmentComplete);
        }

        [Fact]
        public void Path_Desktop_NoEsp_HelloResolves_FullSequence()
        {
            var sm = new CompletionStateMachine();

            // 1. Desktop arrives (no ESP ever seen — WDP v2 or no ESP config)
            var ctx = DefaultContext(enrollmentType: "v2");
            sm.ProcessTrigger("desktop_arrived", ctx);
            Assert.Equal(EnrollmentCompletionState.DesktopArrivedAwaitingHello, sm.CurrentState);

            // 2. Hello resolves
            var helloCtx = DefaultContext(enrollmentType: "v2", isHelloCompleted: true);
            var result = sm.ProcessTrigger("hello_completed", helloCtx);

            Assert.Equal(EnrollmentCompletionState.Completed, sm.CurrentState);
            Assert.True(result.ShouldEmitEnrollmentComplete);
            Assert.Equal("desktop_hello", result.CompletionSource);
        }

        [Fact]
        public void Path_Desktop_EspActive_WaitsForExit_ThenCompletes()
        {
            var sm = new CompletionStateMachine();

            // 1. ESP starts
            sm.ProcessTrigger("esp_phase_changed", DefaultContext());

            // 2. Desktop arrives while ESP active — blocked
            sm.ProcessTrigger("desktop_arrived", DefaultContext(skipUserStatusPage: false));
            Assert.Equal(EnrollmentCompletionState.DesktopArrivedEspBlocking, sm.CurrentState);

            // 3. ESP exits from AccountSetup
            sm.ProcessTrigger("esp_exiting", DefaultContext(lastEspPhase: "AccountSetup"));

            // 4. Hello resolves
            var result = sm.ProcessTrigger("hello_completed", DefaultContext(isHelloCompleted: true));

            Assert.Equal(EnrollmentCompletionState.Completed, sm.CurrentState);
            Assert.True(result.ShouldEmitEnrollmentComplete);
        }

        [Fact]
        public void Path_ImePattern_WaitingForHello_SafetyTimeout()
        {
            var sm = new CompletionStateMachine();

            // 1. ESP starts
            sm.ProcessTrigger("esp_phase_changed", DefaultContext());

            // 2. ESP exits
            sm.ProcessTrigger("esp_exiting", DefaultContext(lastEspPhase: "AccountSetup"));

            // 3. IME user session completes — Hello pending
            var ctx = DefaultContext(isHelloPolicyConfigured: true, isHelloCompleted: false);
            sm.ProcessTrigger("ime_user_session_completed", ctx);
            Assert.Equal(EnrollmentCompletionState.WaitingForHello, sm.CurrentState);

            // 4. Safety timeout fires
            var result = sm.ProcessTrigger("hello_safety_timeout", DefaultContext());

            Assert.Equal(EnrollmentCompletionState.Completed, sm.CurrentState);
            Assert.True(result.ShouldForceMarkHelloCompleted);
            Assert.True(result.ShouldEmitEnrollmentComplete);
        }

        [Fact]
        public void Path_ImePattern_EspSettle_Then_Completes()
        {
            var sm = new CompletionStateMachine();

            // 1. ESP starts
            sm.ProcessTrigger("esp_phase_changed", DefaultContext());

            // 2. ESP exits
            sm.ProcessTrigger("esp_exiting", DefaultContext(lastEspPhase: "AccountSetup"));

            // 3. IME user session completes — ESP categories unresolved
            var ctx = DefaultContext(
                isHelloPolicyConfigured: false, // Hello not configured
                hasUnresolvedEspCategories: true);
            sm.ProcessTrigger("ime_user_session_completed", ctx);
            Assert.Equal(EnrollmentCompletionState.WaitingForEspSettle, sm.CurrentState);

            // 4. Settle timeout
            var result = sm.ProcessTrigger("esp_settle_timeout", DefaultContext());

            Assert.Equal(EnrollmentCompletionState.Completed, sm.CurrentState);
            Assert.True(result.ShouldEmitEnrollmentComplete);
            Assert.Equal("ime_pattern", result.CompletionSource);
        }

        [Fact]
        public void Path_WhiteGlove_Completes()
        {
            var sm = new CompletionStateMachine();

            // 1. ESP starts
            sm.ProcessTrigger("esp_phase_changed", DefaultContext());

            // 2. WhiteGlove detected
            var result = sm.ProcessTrigger("whiteglove_complete", DefaultContext());

            Assert.Equal(EnrollmentCompletionState.WhiteGloveCompleted, sm.CurrentState);
            Assert.True(result.ShouldEmitWhiteGloveComplete);
            Assert.False(result.ShouldEmitEnrollmentComplete);
        }

        // ===== SECTION 3: Edge Case / Idempotency Tests =====

        [Fact]
        public void IdempotentCompletion_SecondCallNoOp()
        {
            var sm = new CompletionStateMachine();
            sm.RestoreState(EnrollmentCompletionState.Completed,
                espEverSeen: true, espFinalExitSeen: true, desktopArrived: true,
                isWaitingForHello: false, isWaitingForEspSettle: false, deferredSource: null);

            // Try all triggers — none should transition
            Assert.False(sm.ProcessTrigger("hello_completed", DefaultContext()).Transitioned);
            Assert.False(sm.ProcessTrigger("desktop_arrived", DefaultContext()).Transitioned);
            Assert.False(sm.ProcessTrigger("esp_phase_changed", DefaultContext()).Transitioned);
            Assert.False(sm.ProcessTrigger("esp_exiting", DefaultContext()).Transitioned);
            Assert.False(sm.ProcessTrigger("esp_failure_terminal", DefaultContext()).Transitioned);
            Assert.False(sm.ProcessTrigger("whiteglove_complete", DefaultContext()).Transitioned);
        }

        [Fact]
        public void DesktopArrival_DuplicateIgnored()
        {
            var sm = new CompletionStateMachine();
            sm.ProcessTrigger("desktop_arrived", DefaultContext(hasHelloTracker: false));
            Assert.Equal(EnrollmentCompletionState.Completed, sm.CurrentState);

            // Second desktop arrival — no-op (terminal)
            var result = sm.ProcessTrigger("desktop_arrived", DefaultContext());
            Assert.False(result.Transitioned);
        }

        [Fact]
        public void WhiteGloveIdempotent_SecondNoOp()
        {
            var sm = new CompletionStateMachine();
            sm.ProcessTrigger("whiteglove_complete", DefaultContext());
            Assert.Equal(EnrollmentCompletionState.WhiteGloveCompleted, sm.CurrentState);

            var result = sm.ProcessTrigger("whiteglove_complete", DefaultContext());
            Assert.False(result.Transitioned);
        }

        [Fact]
        public void EspResumed_ResetsCompletionState_HybridJoin()
        {
            var sm = new CompletionStateMachine();
            sm.ProcessTrigger("esp_phase_changed", DefaultContext());
            sm.ProcessTrigger("esp_exiting", DefaultContext(lastEspPhase: "AccountSetup", isHybridJoin: true));
            Assert.True(sm.EspFinalExitSeen);

            // ESP resumes — resets final exit
            sm.ProcessTrigger("esp_resumed", DefaultContext(isHybridJoin: true));
            Assert.False(sm.EspFinalExitSeen);
            Assert.Equal(EnrollmentCompletionState.EspActive, sm.CurrentState);
        }

        [Fact]
        public void DeviceInfoNotReady_Deferred_ReEvaluated()
        {
            var sm = new CompletionStateMachine();

            // Device setup provisioning completes but device info not collected
            var ctx = DefaultContext(
                autopilotMode: null,
                skipUserStatusPage: null,
                deviceInfoCollected: false);
            var result = sm.ProcessTrigger("device_setup_provisioning_complete", ctx);

            Assert.Equal(EnrollmentCompletionState.DeferredForDeviceInfo, sm.CurrentState);
            Assert.Equal("device_setup_provisioning_complete", result.DeferredSource);

            // Device info collected — now detected as device-only
            var resolvedCtx = DeviceOnlyContext();
            resolvedCtx.DeviceInfoCollected = true;
            result = sm.ProcessTrigger("device_info_collected", resolvedCtx);

            Assert.Equal(EnrollmentCompletionState.Completed, sm.CurrentState);
            Assert.True(result.ShouldEmitEnrollmentComplete);
        }

        [Fact]
        public void DeviceInfoNotReady_Deferred_ReEvaluated_NotDeviceOnly()
        {
            var sm = new CompletionStateMachine();

            var ctx = DefaultContext(
                autopilotMode: null,
                skipUserStatusPage: null,
                deviceInfoCollected: false);
            sm.ProcessTrigger("device_setup_provisioning_complete", ctx);
            Assert.Equal(EnrollmentCompletionState.DeferredForDeviceInfo, sm.CurrentState);

            // Device info collected — NOT device-only, return to Idle
            var resolvedCtx = DefaultContext(
                autopilotMode: 0,
                skipUserStatusPage: false,
                aadJoinedWithUser: true,
                deviceInfoCollected: true);
            var result = sm.ProcessTrigger("device_info_collected", resolvedCtx);

            Assert.Equal(EnrollmentCompletionState.Idle, sm.CurrentState);
            Assert.False(result.ShouldEmitEnrollmentComplete);
        }

        [Fact]
        public void NullTrigger_NoTransition()
        {
            var sm = new CompletionStateMachine();
            var result = sm.ProcessTrigger(null, DefaultContext());
            Assert.False(result.Transitioned);
        }

        [Fact]
        public void EmptyTrigger_NoTransition()
        {
            var sm = new CompletionStateMachine();
            var result = sm.ProcessTrigger("", DefaultContext());
            Assert.False(result.Transitioned);
        }

        [Fact]
        public void UnknownTrigger_NoTransition()
        {
            var sm = new CompletionStateMachine();
            var result = sm.ProcessTrigger("unknown_trigger_xyz", DefaultContext());
            Assert.False(result.Transitioned);
        }

        [Fact]
        public void SignalsRecorded_OnTransition()
        {
            var sm = new CompletionStateMachine();
            var result = sm.ProcessTrigger("esp_phase_changed", DefaultContext());

            Assert.NotNull(result.SignalsToRecord);
            Assert.Contains("esp_phase_changed", result.SignalsToRecord);
        }

        // ===== SECTION 4: Crash Recovery / State Reconstruction Tests =====

        [Fact]
        public void ReconstructState_Completed_ReturnsCompleted()
        {
            var state = CompletionStateMachine.ReconstructStateFromFlags(
                enrollmentCompleteEmitted: true, isWaitingForHello: false,
                isWaitingForEspSettle: false, espFinalExitSeen: true,
                desktopArrived: true, espEverSeen: true, isDeviceOnly: false);

            Assert.Equal(EnrollmentCompletionState.Completed, state);
        }

        [Fact]
        public void ReconstructState_WaitingForHello_ReturnsWaitingForHello()
        {
            var state = CompletionStateMachine.ReconstructStateFromFlags(
                enrollmentCompleteEmitted: false, isWaitingForHello: true,
                isWaitingForEspSettle: false, espFinalExitSeen: true,
                desktopArrived: false, espEverSeen: true, isDeviceOnly: false);

            Assert.Equal(EnrollmentCompletionState.WaitingForHello, state);
        }

        [Fact]
        public void ReconstructState_WaitingForEspSettle_ReturnsWaitingForEspSettle()
        {
            var state = CompletionStateMachine.ReconstructStateFromFlags(
                enrollmentCompleteEmitted: false, isWaitingForHello: false,
                isWaitingForEspSettle: true, espFinalExitSeen: true,
                desktopArrived: false, espEverSeen: true, isDeviceOnly: false);

            Assert.Equal(EnrollmentCompletionState.WaitingForEspSettle, state);
        }

        [Fact]
        public void ReconstructState_DeviceOnly_EspExited_ReturnsDeviceOnly()
        {
            var state = CompletionStateMachine.ReconstructStateFromFlags(
                enrollmentCompleteEmitted: false, isWaitingForHello: false,
                isWaitingForEspSettle: false, espFinalExitSeen: true,
                desktopArrived: false, espEverSeen: true, isDeviceOnly: true);

            Assert.Equal(EnrollmentCompletionState.DeviceOnlyAwaitingCompletion, state);
        }

        [Fact]
        public void ReconstructState_EspExited_Desktop_ReturnsDesktopAwaitingHello()
        {
            var state = CompletionStateMachine.ReconstructStateFromFlags(
                enrollmentCompleteEmitted: false, isWaitingForHello: false,
                isWaitingForEspSettle: false, espFinalExitSeen: true,
                desktopArrived: true, espEverSeen: true, isDeviceOnly: false);

            Assert.Equal(EnrollmentCompletionState.DesktopArrivedAwaitingHello, state);
        }

        [Fact]
        public void ReconstructState_EspExited_NoDesktop_ReturnsEspExitedAwaiting()
        {
            var state = CompletionStateMachine.ReconstructStateFromFlags(
                enrollmentCompleteEmitted: false, isWaitingForHello: false,
                isWaitingForEspSettle: false, espFinalExitSeen: true,
                desktopArrived: false, espEverSeen: true, isDeviceOnly: false);

            Assert.Equal(EnrollmentCompletionState.EspExitedAwaitingCompletion, state);
        }

        [Fact]
        public void ReconstructState_EspActive_Desktop_ReturnsDesktopEspBlocking()
        {
            var state = CompletionStateMachine.ReconstructStateFromFlags(
                enrollmentCompleteEmitted: false, isWaitingForHello: false,
                isWaitingForEspSettle: false, espFinalExitSeen: false,
                desktopArrived: true, espEverSeen: true, isDeviceOnly: false);

            Assert.Equal(EnrollmentCompletionState.DesktopArrivedEspBlocking, state);
        }

        [Fact]
        public void ReconstructState_EspActive_NoDesktop_ReturnsEspActive()
        {
            var state = CompletionStateMachine.ReconstructStateFromFlags(
                enrollmentCompleteEmitted: false, isWaitingForHello: false,
                isWaitingForEspSettle: false, espFinalExitSeen: false,
                desktopArrived: false, espEverSeen: true, isDeviceOnly: false);

            Assert.Equal(EnrollmentCompletionState.EspActive, state);
        }

        [Fact]
        public void ReconstructState_DesktopOnly_ReturnsDesktopAwaitingHello()
        {
            var state = CompletionStateMachine.ReconstructStateFromFlags(
                enrollmentCompleteEmitted: false, isWaitingForHello: false,
                isWaitingForEspSettle: false, espFinalExitSeen: false,
                desktopArrived: true, espEverSeen: false, isDeviceOnly: false);

            Assert.Equal(EnrollmentCompletionState.DesktopArrivedAwaitingHello, state);
        }

        [Fact]
        public void ReconstructState_Fresh_ReturnsIdle()
        {
            var state = CompletionStateMachine.ReconstructStateFromFlags(
                enrollmentCompleteEmitted: false, isWaitingForHello: false,
                isWaitingForEspSettle: false, espFinalExitSeen: false,
                desktopArrived: false, espEverSeen: false, isDeviceOnly: false);

            Assert.Equal(EnrollmentCompletionState.Idle, state);
        }

        [Fact]
        public void RestoreState_PreservesAllFields()
        {
            var sm = new CompletionStateMachine();
            sm.RestoreState(EnrollmentCompletionState.EspExitedAwaitingCompletion,
                espEverSeen: true, espFinalExitSeen: true, desktopArrived: true,
                isWaitingForHello: false, isWaitingForEspSettle: false, deferredSource: null);

            Assert.Equal(EnrollmentCompletionState.EspExitedAwaitingCompletion, sm.CurrentState);
            Assert.True(sm.EspEverSeen);
            Assert.True(sm.EspFinalExitSeen);
            Assert.True(sm.DesktopArrived);
        }

        [Fact]
        public void RestoreState_WaitingForHello_CanResumeCompletion()
        {
            var sm = new CompletionStateMachine();
            sm.RestoreState(EnrollmentCompletionState.WaitingForHello,
                espEverSeen: true, espFinalExitSeen: true, desktopArrived: false,
                isWaitingForHello: true, isWaitingForEspSettle: false, deferredSource: null);

            // Hello completes after crash recovery
            var result = sm.ProcessTrigger("hello_completed", DefaultContext(isHelloCompleted: true));

            Assert.Equal(EnrollmentCompletionState.Completed, sm.CurrentState);
            Assert.True(result.ShouldEmitEnrollmentComplete);
            Assert.Equal("ime_hello", result.CompletionSource);
        }

        [Fact]
        public void RestoreState_WaitingForEspSettle_CanResumeCompletion()
        {
            var sm = new CompletionStateMachine();
            sm.RestoreState(EnrollmentCompletionState.WaitingForEspSettle,
                espEverSeen: true, espFinalExitSeen: true, desktopArrived: false,
                isWaitingForHello: false, isWaitingForEspSettle: true, deferredSource: null);

            // Settle timeout fires after crash recovery
            var result = sm.ProcessTrigger("esp_settle_timeout", DefaultContext());

            Assert.Equal(EnrollmentCompletionState.Completed, sm.CurrentState);
            Assert.True(result.ShouldEmitEnrollmentComplete);
        }

        // ===== SECTION 5: Extension Method Tests =====

        [Fact]
        public void IsTerminal_Completed_ReturnsTrue()
        {
            Assert.True(EnrollmentCompletionState.Completed.IsTerminal());
        }

        [Fact]
        public void IsTerminal_Failed_ReturnsTrue()
        {
            Assert.True(EnrollmentCompletionState.Failed.IsTerminal());
        }

        [Fact]
        public void IsTerminal_WhiteGloveCompleted_ReturnsTrue()
        {
            Assert.True(EnrollmentCompletionState.WhiteGloveCompleted.IsTerminal());
        }

        [Fact]
        public void IsTerminal_Idle_ReturnsFalse()
        {
            Assert.False(EnrollmentCompletionState.Idle.IsTerminal());
        }

        [Fact]
        public void IsTerminal_EspActive_ReturnsFalse()
        {
            Assert.False(EnrollmentCompletionState.EspActive.IsTerminal());
        }

        [Fact]
        public void IsTerminal_WaitingForHello_ReturnsFalse()
        {
            Assert.False(EnrollmentCompletionState.WaitingForHello.IsTerminal());
        }

        [Fact]
        public void IsWaiting_WaitingForHello_ReturnsTrue()
        {
            Assert.True(EnrollmentCompletionState.WaitingForHello.IsWaiting());
        }

        [Fact]
        public void IsWaiting_WaitingForEspSettle_ReturnsTrue()
        {
            Assert.True(EnrollmentCompletionState.WaitingForEspSettle.IsWaiting());
        }

        [Fact]
        public void IsWaiting_DeviceOnlySafetyWait_ReturnsTrue()
        {
            Assert.True(EnrollmentCompletionState.DeviceOnlySafetyWait.IsWaiting());
        }

        [Fact]
        public void IsWaiting_HybridRebootGateBlocked_ReturnsTrue()
        {
            Assert.True(EnrollmentCompletionState.HybridRebootGateBlocked.IsWaiting());
        }

        [Fact]
        public void IsWaiting_DeferredForDeviceInfo_ReturnsTrue()
        {
            Assert.True(EnrollmentCompletionState.DeferredForDeviceInfo.IsWaiting());
        }

        [Fact]
        public void IsWaiting_Idle_ReturnsFalse()
        {
            Assert.False(EnrollmentCompletionState.Idle.IsWaiting());
        }

        [Fact]
        public void IsWaiting_Completed_ReturnsFalse()
        {
            Assert.False(EnrollmentCompletionState.Completed.IsWaiting());
        }

        [Fact]
        public void IsWaiting_EspActive_ReturnsFalse()
        {
            Assert.False(EnrollmentCompletionState.EspActive.IsWaiting());
        }

        // ===== SECTION 5: Drift regression tests =====
        // Repro for session 1fda3b0a-e586-4980-bc90-491e4e8ca870:
        // ESP phase events were not persisted before reboot, so SM was restored with
        // espEverSeen=false. Subsequent esp_exiting + desktop_arrived must not complete
        // the SM prematurely — the ProcessTrigger snapshot sync lifts the missing flags
        // from the context (the authoritative tracker state).

        [Fact]
        public void Drift_RestoredWithoutEspEverSeen_EspExitingSyncsFromContext()
        {
            var sm = new CompletionStateMachine();
            sm.RestoreState(EnrollmentCompletionState.Idle,
                espEverSeen: false, espFinalExitSeen: false, desktopArrived: false,
                isWaitingForHello: false, isWaitingForEspSettle: false, deferredSource: null);

            // ctx says ESP was seen (real tracker is the source of truth).
            // Hello policy IS configured but not yet completed — must wait, not complete.
            var ctx = DefaultContext(
                lastEspPhase: "AccountSetup",
                isHelloPolicyConfigured: true,
                isHelloCompleted: false);
            ctx.EspEverSeen = true;
            ctx.EspFinalExitSeen = false;

            var result = sm.ProcessTrigger("esp_exiting", ctx);

            Assert.True(result.Transitioned);
            Assert.NotEqual(EnrollmentCompletionState.Completed, sm.CurrentState);
            Assert.False(result.ShouldEmitEnrollmentComplete);
            Assert.True(sm.EspEverSeen, "SM must lift _espEverSeen from ctx via the sync block");
            Assert.True(sm.EspFinalExitSeen, "esp_exiting Path A must mark final exit");
        }

        [Fact]
        public void Drift_RestoredWithoutEspEverSeen_DesktopArrivedRoutesToEspBlocking()
        {
            // The exact scenario from session 1fda3b0a-e586-4980-bc90-491e4e8ca870 at 06:05:41:
            // SM restored with espEverSeen=false, but ctx says ESP IS active.
            // Pre-fix: SM took the "no ESP" else branch and reached Completed.
            // Post-fix: sync block lifts _espEverSeen from ctx, SM correctly routes to
            // DesktopArrivedEspBlocking.
            var sm = new CompletionStateMachine();
            sm.RestoreState(EnrollmentCompletionState.Idle,
                espEverSeen: false, espFinalExitSeen: false, desktopArrived: false,
                isWaitingForHello: false, isWaitingForEspSettle: false, deferredSource: null);

            var ctx = DefaultContext(
                isHelloPolicyConfigured: true,
                isHelloCompleted: false);
            ctx.EspEverSeen = true;
            ctx.EspFinalExitSeen = false;

            var result = sm.ProcessTrigger("desktop_arrived", ctx);

            Assert.NotEqual(EnrollmentCompletionState.Completed, sm.CurrentState);
            Assert.Equal(EnrollmentCompletionState.DesktopArrivedEspBlocking, sm.CurrentState);
            Assert.False(result.ShouldEmitEnrollmentComplete);
        }

        // ===== WhiteGlove Guard Tests =====

        [Fact]
        public void DeviceOnly_ProvisioningComplete_WithWhiteGloveStartOnly_CompletesAsSelfDeploying()
        {
            // EventID 509 alone (soft signal) must NOT trigger WhiteGlove — fires on hybrid-join too
            var sm = new CompletionStateMachine();
            var ctx = DeviceOnlyContext();
            ctx.WhiteGloveStartDetected = true;
            ctx.HasSaveWhiteGloveSuccessResult = false;

            var result = sm.ProcessTrigger("device_setup_provisioning_complete", ctx);

            Assert.Equal(EnrollmentCompletionState.Completed, sm.CurrentState);
            Assert.True(result.ShouldEmitEnrollmentComplete);
            Assert.False(result.ShouldEmitWhiteGloveComplete);
        }

        [Fact]
        public void DeviceOnly_ProvisioningComplete_WithOnlySaveWhiteGloveSuccessResult_DoesNotTriggerWhiteGlove()
        {
            // Post-classifier behaviour: HasSaveWhiteGloveSuccessResult alone scores 10 (very
            // weak — it also fires on genuinely non-WG devices). With DeviceOnly that reaches
            // 25 → Confidence.None → NOT routed to WhiteGlove. This is the core bugfix.
            var sm = new CompletionStateMachine();
            var ctx = DeviceOnlyContext();
            ctx.HasSaveWhiteGloveSuccessResult = true;

            var result = sm.ProcessTrigger("device_setup_provisioning_complete", ctx);

            Assert.NotEqual(EnrollmentCompletionState.WhiteGloveCompleted, sm.CurrentState);
            Assert.False(result.ShouldEmitWhiteGloveComplete);
            Assert.True(result.ShouldEmitEnrollmentComplete);
        }

        [Fact]
        public void DeviceOnly_ProvisioningComplete_BothWgSignals_IsWeak_DoesNotTriggerWhiteGlove()
        {
            // SaveWg(+10) + Event509(+15) + DeviceOnly(+15) = 40 → Confidence.Weak → NOT WG
            // (asymmetric-conservative routing: unsicher → kein WG).
            var sm = new CompletionStateMachine();
            var ctx = DeviceOnlyContext();
            ctx.WhiteGloveStartDetected = true;
            ctx.HasSaveWhiteGloveSuccessResult = true;

            var result = sm.ProcessTrigger("device_setup_provisioning_complete", ctx);

            Assert.NotEqual(EnrollmentCompletionState.WhiteGloveCompleted, sm.CurrentState);
            Assert.False(result.ShouldEmitWhiteGloveComplete);
        }

        [Fact]
        public void DeviceOnly_ProvisioningComplete_ShellCoreWgSuccess_TransitionsToWhiteGloveCompleted()
        {
            // ShellCoreWhiteGloveSuccess (Event 62407) alone scores 80 → Confidence.Strong → WG.
            var sm = new CompletionStateMachine();
            var ctx = DeviceOnlyContext();
            ctx.ShellCoreWhiteGloveSuccess = true;

            var result = sm.ProcessTrigger("device_setup_provisioning_complete", ctx);

            Assert.Equal(EnrollmentCompletionState.WhiteGloveCompleted, sm.CurrentState);
            Assert.True(result.ShouldEmitWhiteGloveComplete);
            Assert.False(result.ShouldEmitEnrollmentComplete);
        }

        [Fact]
        public void DeviceOnly_ProvisioningComplete_WgSignalVerbund_ReachingThreshold_TransitionsToWhiteGlove()
        {
            // Signalverbund ohne ShellCore: SaveWg(+10) + Event509(+15) + FooUser(+20) +
            // DeviceOnly(+15) + AgentRestartAfterEsp(+10) = 70 → Strong → WG.
            var sm = new CompletionStateMachine();
            var ctx = DeviceOnlyContext();
            ctx.WhiteGloveStartDetected = true;
            ctx.HasSaveWhiteGloveSuccessResult = true;
            ctx.IsFooUserDetected = true;
            ctx.EspFinalExitUtc = DateTime.UtcNow.AddHours(-1);
            ctx.AgentStartTimeUtc = DateTime.UtcNow.AddMinutes(-30);

            var result = sm.ProcessTrigger("device_setup_provisioning_complete", ctx);

            Assert.Equal(EnrollmentCompletionState.WhiteGloveCompleted, sm.CurrentState);
            Assert.True(result.ShouldEmitWhiteGloveComplete);
        }

        [Fact]
        public void DeviceOnly_ProvisioningComplete_LateAadJoin_DoesNotTriggerWhiteGlove()
        {
            // Selbst mit starkem WG-Signal: realer AAD-Join (späte Erkennung via AadJoinWatcher)
            // muss Classifier-Hard-Excluder auslösen → NICHT WG.
            var sm = new CompletionStateMachine();
            var ctx = DeviceOnlyContext();
            ctx.ShellCoreWhiteGloveSuccess = true;
            ctx.AadJoinedWithUser = true; // late AAD join flipped this

            var result = sm.ProcessTrigger("device_setup_provisioning_complete", ctx);

            Assert.NotEqual(EnrollmentCompletionState.WhiteGloveCompleted, sm.CurrentState);
            Assert.False(result.ShouldEmitWhiteGloveComplete);
        }

        [Fact]
        public void DeviceOnly_ProvisioningComplete_NoWhiteGloveSignals_CompletesAsSelfDeploying()
        {
            var sm = new CompletionStateMachine();
            var ctx = DeviceOnlyContext();
            ctx.WhiteGloveStartDetected = false;
            ctx.HasSaveWhiteGloveSuccessResult = false;

            var result = sm.ProcessTrigger("device_setup_provisioning_complete", ctx);

            Assert.Equal(EnrollmentCompletionState.Completed, sm.CurrentState);
            Assert.True(result.ShouldEmitEnrollmentComplete);
            Assert.False(result.ShouldEmitWhiteGloveComplete);
            Assert.Equal("self_deploying_provisioning_complete", result.CompletionSource);
        }

        [Fact]
        public void NonDeviceOnly_ProvisioningComplete_WithWhiteGloveStart_DoesNotTriggerWhiteGlove()
        {
            // Non-device-only deployments should NOT activate the WhiteGlove guard
            var sm = new CompletionStateMachine();
            var ctx = DefaultContext(whiteGloveStartDetected: true);

            var result = sm.ProcessTrigger("device_setup_provisioning_complete", ctx);

            Assert.NotEqual(EnrollmentCompletionState.WhiteGloveCompleted, sm.CurrentState);
            Assert.False(result.ShouldEmitWhiteGloveComplete);
        }

        [Fact]
        public void WhiteGloveCompleted_IsTerminal()
        {
            // Use ShellCoreWhiteGloveSuccess (score 80) to drive into WG — post-classifier
            // it's the only single signal that hits Strong on its own.
            var sm = new CompletionStateMachine();
            var ctx = DeviceOnlyContext();
            ctx.ShellCoreWhiteGloveSuccess = true;
            sm.ProcessTrigger("device_setup_provisioning_complete", ctx);

            Assert.Equal(EnrollmentCompletionState.WhiteGloveCompleted, sm.CurrentState);

            // Further triggers should be no-ops
            var result2 = sm.ProcessTrigger("esp_phase_changed", DefaultContext());
            Assert.False(result2.Transitioned);
            Assert.Equal(EnrollmentCompletionState.WhiteGloveCompleted, sm.CurrentState);
        }

        [Fact]
        public void EspExiting_SkipUser_WithWhiteGloveStartOnly_CompletesAsDeviceOnly()
        {
            // EventID 509 alone (soft signal) must NOT trigger WhiteGlove on ESP exit
            var sm = new CompletionStateMachine();

            var ctx = DefaultContext(autopilotMode: 0, skipUserStatusPage: true, aadJoinedWithUser: false,
                isHelloPolicyConfigured: false, whiteGloveStartDetected: true);
            ctx.HasSaveWhiteGloveSuccessResult = false;
            sm.ProcessTrigger("esp_phase_changed", ctx);

            ctx.LastEspPhase = "DeviceSetup";
            var result = sm.ProcessTrigger("esp_exiting", ctx);

            Assert.NotEqual(EnrollmentCompletionState.WhiteGloveCompleted, sm.CurrentState);
            Assert.False(result.ShouldEmitWhiteGloveComplete);
        }

        [Fact]
        public void EspExiting_SkipUser_WithOnlySaveWhiteGloveSuccessResult_DoesNotTriggerWhiteGlove()
        {
            // Post-classifier behaviour: weak single signal (score 25 with device-only) → NOT WG.
            var sm = new CompletionStateMachine();

            var ctx = DefaultContext(autopilotMode: 0, skipUserStatusPage: true, aadJoinedWithUser: false,
                isHelloPolicyConfigured: false, hasSaveWhiteGloveSuccessResult: true);
            sm.ProcessTrigger("esp_phase_changed", ctx);

            ctx.LastEspPhase = "DeviceSetup";
            var result = sm.ProcessTrigger("esp_exiting", ctx);

            Assert.NotEqual(EnrollmentCompletionState.WhiteGloveCompleted, sm.CurrentState);
            Assert.False(result.ShouldEmitWhiteGloveComplete);
        }

        [Fact]
        public void EspExiting_SkipUser_WithShellCoreWgSuccess_TransitionsToWhiteGloveCompleted()
        {
            // ShellCoreWhiteGloveSuccess alone reaches Strong → WG.
            var sm = new CompletionStateMachine();

            var ctx = DefaultContext(autopilotMode: 0, skipUserStatusPage: true, aadJoinedWithUser: false,
                isHelloPolicyConfigured: false);
            ctx.ShellCoreWhiteGloveSuccess = true;
            sm.ProcessTrigger("esp_phase_changed", ctx);

            ctx.LastEspPhase = "DeviceSetup";
            var result = sm.ProcessTrigger("esp_exiting", ctx);

            Assert.Equal(EnrollmentCompletionState.WhiteGloveCompleted, sm.CurrentState);
            Assert.True(result.ShouldEmitWhiteGloveComplete);
        }

        [Fact]
        public void EspExiting_SkipUser_NoWhiteGloveSignals_CompletesAsDeviceOnly()
        {
            var sm = new CompletionStateMachine();

            var ctx = DefaultContext(autopilotMode: 0, skipUserStatusPage: true, aadJoinedWithUser: false,
                isHelloPolicyConfigured: false, isHelloCompleted: true);
            sm.ProcessTrigger("esp_phase_changed", ctx);

            ctx.LastEspPhase = "DeviceSetup";
            var result = sm.ProcessTrigger("esp_exiting", ctx);

            Assert.NotEqual(EnrollmentCompletionState.WhiteGloveCompleted, sm.CurrentState);
            Assert.False(result.ShouldEmitWhiteGloveComplete);
        }
    }
}
