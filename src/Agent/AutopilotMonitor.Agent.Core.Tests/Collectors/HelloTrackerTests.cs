using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.Core.Tests.Helpers;
using AutopilotMonitor.Shared.Models;
using Xunit;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Flows;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Completion;

namespace AutopilotMonitor.Agent.Core.Tests.Collectors
{
    /// <summary>
    /// Tests for <see cref="HelloTracker"/>. Drives event processing via the internal
    /// ProcessHelloEvent / ProcessHelloForBusinessEvent seams (EventLogWatcher is Windows-only
    /// and not mocked) and timers via TriggerWait/CompletionTimeoutForTest to stay deterministic.
    /// </summary>
    public sealed class HelloTrackerTests
    {
        private readonly ConcurrentBag<EnrollmentEvent> _captured = new ConcurrentBag<EnrollmentEvent>();
        private int _helloCompletedInvocations;

        private HelloTracker CreateTracker(int helloWaitTimeoutSeconds = 30)
        {
            var tracker = new HelloTracker(
                sessionId: "sess-1",
                tenantId: "tenant-1",
                onEventCollected: e => _captured.Add(e),
                logger: TestLogger.Instance,
                helloWaitTimeoutSeconds: helloWaitTimeoutSeconds);
            tracker.HelloCompleted += (_, __) => System.Threading.Interlocked.Increment(ref _helloCompletedInvocations);
            return tracker;
        }

        private List<EnrollmentEvent> Captured => _captured.ToList();
        private EnrollmentEvent Last(string eventType) => Captured.LastOrDefault(e => e.EventType == eventType);

        // =====================================================================
        // UDR terminal events (300 / 301 / 362)
        // =====================================================================

        [Fact]
        public void ProcessHelloEvent_300_MarksCompletedAndFiresEvent()
        {
            var t = CreateTracker();

            t.ProcessHelloEvent(300, DateTime.UtcNow, "WinHelloForBusinessCsp", isBackfill: false);

            Assert.True(t.IsHelloCompleted);
            Assert.Equal("completed", t.HelloOutcome);
            Assert.Equal(1, _helloCompletedInvocations);
            var evt = Last("hello_provisioning_completed");
            Assert.NotNull(evt);
            Assert.Equal(EventSeverity.Info, evt.Severity);
            Assert.True(evt.ImmediateUpload);
        }

        [Fact]
        public void ProcessHelloEvent_301_MarksCompletedWithFailedEventType()
        {
            var t = CreateTracker();

            t.ProcessHelloEvent(301, DateTime.UtcNow, "WinHelloForBusinessCsp", isBackfill: false);

            Assert.True(t.IsHelloCompleted);
            Assert.Equal("completed", t.HelloOutcome);
            Assert.Equal(1, _helloCompletedInvocations);
            var evt = Last("hello_provisioning_failed");
            Assert.NotNull(evt);
            Assert.Equal(EventSeverity.Error, evt.Severity);
            Assert.True(evt.ImmediateUpload);
        }

        [Fact]
        public void ProcessHelloEvent_362_MarksCompletedAsBlocked()
        {
            var t = CreateTracker();

            t.ProcessHelloEvent(362, DateTime.UtcNow, "prov", false);

            Assert.True(t.IsHelloCompleted);
            Assert.Equal(1, _helloCompletedInvocations);
            var evt = Last("hello_provisioning_blocked");
            Assert.NotNull(evt);
            Assert.Equal(EventSeverity.Warning, evt.Severity);
        }

        [Fact]
        public void ProcessHelloEvent_360_IsSnapshotOnly_DoesNotMarkCompleted()
        {
            var t = CreateTracker();

            t.ProcessHelloEvent(360, DateTime.UtcNow, "prov", false);

            Assert.False(t.IsHelloCompleted);
            Assert.Null(t.HelloOutcome);
            Assert.Equal(0, _helloCompletedInvocations);
            Assert.NotNull(Last("hello_provisioning_willnotlaunch"));
        }

        [Fact]
        public void ProcessHelloEvent_358_EmitsWillLaunch_NoCompletion()
        {
            var t = CreateTracker();

            t.ProcessHelloEvent(358, DateTime.UtcNow, "prov", false);

            Assert.False(t.IsHelloCompleted);
            Assert.Equal(0, _helloCompletedInvocations);
            Assert.NotNull(Last("hello_provisioning_willlaunch"));
        }

        [Fact]
        public void ProcessHelloEvent_DuplicateTerminal_DoesNotRefireHelloCompleted()
        {
            var t = CreateTracker();

            t.ProcessHelloEvent(300, DateTime.UtcNow, "prov", false);
            t.ProcessHelloEvent(301, DateTime.UtcNow.AddSeconds(1), "prov", false);

            Assert.Equal(1, _helloCompletedInvocations); // second call sees _isHelloCompleted=true
        }

        [Fact]
        public void ProcessHelloEvent_BackfillFlag_AddedToData()
        {
            var t = CreateTracker();

            t.ProcessHelloEvent(300, DateTime.UtcNow, "prov", isBackfill: true);

            var evt = Last("hello_provisioning_completed");
            Assert.True(evt.Data.ContainsKey("backfill"));
            Assert.Equal(true, evt.Data["backfill"]);
        }

        // =====================================================================
        // HelloForBusiness events (3024 / 6045)
        // =====================================================================

        [Fact]
        public void ProcessHelloForBusinessEvent_3024_EmitsProcessingStarted_NoCompletion()
        {
            var t = CreateTracker();

            t.ProcessHelloForBusinessEvent(3024, DateTime.UtcNow, "Hello processing started", "provider", false);

            Assert.False(t.IsHelloCompleted);
            Assert.Equal(0, _helloCompletedInvocations);
            Assert.NotNull(Last("hello_processing_started"));
        }

        [Fact]
        public void ProcessHelloForBusinessEvent_6045_WithUserSkipHResult_MarksSkipped()
        {
            var t = CreateTracker();

            t.ProcessHelloForBusinessEvent(
                6045, DateTime.UtcNow,
                "User cancelled Hello for Business: HRESULT 0x801C044F",
                "provider", false);

            Assert.True(t.IsHelloCompleted);
            Assert.Equal("skipped", t.HelloOutcome);
            Assert.Equal(1, _helloCompletedInvocations);
            var evt = Last("hello_skipped");
            Assert.NotNull(evt);
            Assert.Equal(EventSeverity.Warning, evt.Severity);
            Assert.Equal("0x801C044F", evt.Data["hresult"]);
        }

        [Fact]
        public void ProcessHelloForBusinessEvent_6045_WithOtherHResult_DoesNotMarkCompleted()
        {
            var t = CreateTracker();

            t.ProcessHelloForBusinessEvent(
                6045, DateTime.UtcNow,
                "Hello processing stopped: HRESULT 0x80070005",
                "provider", false);

            Assert.False(t.IsHelloCompleted);
            Assert.Equal(0, _helloCompletedInvocations);
            Assert.NotNull(Last("hello_processing_stopped"));
        }

        [Fact]
        public void ExtractHResultFromDescription_MatchesValidHex()
        {
            Assert.Equal("0x801C044F",
                HelloTracker.ExtractHResultFromDescription("something HRESULT 0x801C044F tail"));
            Assert.Null(HelloTracker.ExtractHResultFromDescription("no hresult here"));
            Assert.Null(HelloTracker.ExtractHResultFromDescription(null));
            Assert.Null(HelloTracker.ExtractHResultFromDescription(""));
        }

        // =====================================================================
        // Wait timer — policy-enabled path (extend to completion timer)
        // =====================================================================

        [Fact]
        public void WaitTimeout_WithPolicyEnabled_EmitsExtendedWaitAndArmsCompletionTimer()
        {
            var t = CreateTracker();
            t.SetPolicyForTest(helloEnabled: true, source: "GPO");
            t.StartHelloWaitTimer();

            t.TriggerWaitTimeoutForTest();

            Assert.False(t.IsHelloCompleted);
            Assert.Equal(0, _helloCompletedInvocations);
            Assert.True(t.IsCompletionTimerActiveForTest);
            var evt = Last("hello_wait_timeout");
            Assert.NotNull(evt);
            Assert.Equal("extended_wait", evt.Data["action"]);
            Assert.Equal(true, evt.Data["helloPolicyEnabled"]);
        }

        // =====================================================================
        // Wait timer — policy-disabled path (immediate completion)
        // =====================================================================

        [Fact]
        public void WaitTimeout_WithoutPolicyConfigured_MarksNotConfiguredAndFiresCompleted()
        {
            var t = CreateTracker();
            t.StartHelloWaitTimer();

            t.TriggerWaitTimeoutForTest();

            Assert.True(t.IsHelloCompleted);
            Assert.Equal("not_configured", t.HelloOutcome);
            Assert.Equal(1, _helloCompletedInvocations);
            var evt = Last("hello_wait_timeout");
            Assert.NotNull(evt);
            Assert.Equal("enrollment_complete", evt.Data["action"]);
        }

        [Fact]
        public void WaitTimeout_WithPolicyDetectedAsDisabled_MarksNotConfigured()
        {
            var t = CreateTracker();
            t.SetPolicyForTest(helloEnabled: false, source: "GPO");
            t.StartHelloWaitTimer();

            t.TriggerWaitTimeoutForTest();

            Assert.True(t.IsHelloCompleted);
            Assert.Equal("not_configured", t.HelloOutcome);
            Assert.Equal(1, _helloCompletedInvocations);
        }

        [Fact]
        public void WaitTimeout_AfterWizardStarted_IsIgnored()
        {
            var t = CreateTracker();
            t.NotifyHelloWizardStarted();

            t.TriggerWaitTimeoutForTest();

            Assert.False(t.IsHelloCompleted);
            Assert.Equal(0, _helloCompletedInvocations);
        }

        [Fact]
        public void WaitTimeout_AfterHelloAlreadyCompleted_IsIgnored()
        {
            var t = CreateTracker();
            t.ProcessHelloEvent(300, DateTime.UtcNow, "prov", false);
            var initialCount = _helloCompletedInvocations;

            t.TriggerWaitTimeoutForTest();

            Assert.Equal(initialCount, _helloCompletedInvocations);
        }

        // =====================================================================
        // Completion timer
        // =====================================================================

        [Fact]
        public void CompletionTimeout_AfterWizardStart_MarksTimeout()
        {
            var t = CreateTracker();
            t.NotifyHelloWizardStarted();

            t.TriggerCompletionTimeoutForTest();

            Assert.True(t.IsHelloCompleted);
            Assert.Equal("timeout", t.HelloOutcome);
            Assert.Equal(1, _helloCompletedInvocations);
            Assert.NotNull(Last("hello_completion_timeout"));
        }

        [Fact]
        public void CompletionTimeout_WithoutWizardStart_MarksWizardNotStarted()
        {
            var t = CreateTracker();

            t.TriggerCompletionTimeoutForTest();

            Assert.True(t.IsHelloCompleted);
            Assert.Equal("wizard_not_started", t.HelloOutcome);
        }

        // =====================================================================
        // Cross-boundary notifications
        // =====================================================================

        [Fact]
        public void NotifyHelloWizardStarted_StopsWaitTimer_StartsCompletionTimer()
        {
            var t = CreateTracker();
            t.StartHelloWaitTimer();
            Assert.True(t.IsWaitTimerActiveForTest);

            t.NotifyHelloWizardStarted();

            Assert.False(t.IsWaitTimerActiveForTest);
            Assert.True(t.IsCompletionTimerActiveForTest);
            Assert.True(t.IsHelloWizardStartedForTest);
        }

        [Fact]
        public void NotifyHelloWizardStarted_Idempotent()
        {
            var t = CreateTracker();

            t.NotifyHelloWizardStarted();
            t.NotifyHelloWizardStarted();

            Assert.True(t.IsHelloWizardStartedForTest);
        }

        [Fact]
        public void NotifyEspExited_PopulatedIntoWaitTimeoutDataField()
        {
            var t = CreateTracker();
            t.NotifyEspExited();
            t.StartHelloWaitTimer();

            t.TriggerWaitTimeoutForTest();

            var evt = Last("hello_wait_timeout");
            Assert.Equal(true, evt.Data["espExitDetected"]);
        }

        // =====================================================================
        // External coordination API
        // =====================================================================

        [Fact]
        public void ForceMarkHelloCompleted_SetsOutcome_DoesNotFireEvent()
        {
            var t = CreateTracker();

            t.ForceMarkHelloCompleted("device_only_timeout");

            Assert.True(t.IsHelloCompleted);
            Assert.Equal("device_only_timeout", t.HelloOutcome);
            Assert.Equal(0, _helloCompletedInvocations); // Force does NOT invoke callback
        }

        [Fact]
        public void ForceMarkHelloCompleted_WhenAlreadyCompleted_IsNoOp()
        {
            var t = CreateTracker();
            t.ProcessHelloEvent(300, DateTime.UtcNow, "prov", false);
            var initialOutcome = t.HelloOutcome;

            t.ForceMarkHelloCompleted("override_attempt");

            Assert.Equal(initialOutcome, t.HelloOutcome); // No override once terminal
        }

        [Fact]
        public void ResetForEspResumption_ClearsAllHelloStateAndOutcome()
        {
            var t = CreateTracker();
            t.NotifyHelloWizardStarted();
            t.NotifyEspExited();
            t.ProcessHelloEvent(300, DateTime.UtcNow, "prov", false);
            Assert.True(t.IsHelloCompleted);

            t.ResetForEspResumption();

            Assert.False(t.IsHelloCompleted);
            Assert.Null(t.HelloOutcome);
            Assert.False(t.IsHelloWizardStartedForTest);
            Assert.False(t.IsWaitTimerActiveForTest);
            Assert.False(t.IsCompletionTimerActiveForTest);
        }

        [Fact]
        public void StartHelloWaitTimer_WhenAlreadyCompleted_IsNoOp()
        {
            var t = CreateTracker();
            t.ProcessHelloEvent(300, DateTime.UtcNow, "prov", false);

            t.StartHelloWaitTimer();

            Assert.False(t.IsWaitTimerActiveForTest);
        }

        [Fact]
        public void StartHelloWaitTimer_WhenWizardAlreadyStarted_IsNoOp()
        {
            var t = CreateTracker();
            t.NotifyHelloWizardStarted();

            t.StartHelloWaitTimer();

            Assert.False(t.IsWaitTimerActiveForTest);
        }

        [Fact]
        public void StartHelloWaitTimer_CalledTwice_DoesNotStartSecondTimer()
        {
            var t = CreateTracker();
            t.StartHelloWaitTimer();
            Assert.True(t.IsWaitTimerActiveForTest);

            t.StartHelloWaitTimer(); // no-op

            Assert.True(t.IsWaitTimerActiveForTest);
        }

        // =====================================================================
        // Policy detection emission
        // =====================================================================

        [Fact]
        public void SetPolicyForTest_Enabled_EmitsPolicyDetectedEvent()
        {
            var t = CreateTracker();

            t.SetPolicyForTest(helloEnabled: true, source: "CSP/Intune (device-scoped)");

            Assert.True(t.IsPolicyConfigured);
            var evt = Last("hello_policy_detected");
            Assert.NotNull(evt);
            Assert.Equal(true, evt.Data["helloEnabled"]);
            Assert.Equal("CSP/Intune (device-scoped)", evt.Data["policySource"]);
        }
    }
}
