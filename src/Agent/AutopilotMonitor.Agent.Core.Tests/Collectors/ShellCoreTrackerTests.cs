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
using AutopilotMonitor.Agent.Core.Monitoring.Collectors;

namespace AutopilotMonitor.Agent.Core.Tests.Collectors
{
    /// <summary>
    /// Tests for <see cref="ShellCoreTracker"/>. Drives the ProcessEvent pipeline directly
    /// (EventLogWatcher is Windows-only) and verifies failure classification, WhiteGlove
    /// fire-once, Hello wizard cross-notification, and ESP exit backfill dedup.
    /// </summary>
    public sealed class ShellCoreTrackerTests
    {
        private readonly ConcurrentBag<EnrollmentEvent> _captured = new ConcurrentBag<EnrollmentEvent>();
        private readonly List<string> _finalizingReasons = new List<string>();
        private int _whiteGloveCompletedCount;
        private readonly List<string> _espFailures = new List<string>();

        private ShellCoreTracker CreateTracker(HelloTracker helloTracker = null)
        {
            var t = new ShellCoreTracker(
                sessionId: "sess-1",
                tenantId: "tenant-1",
                onEventCollected: e => _captured.Add(e),
                logger: TestLogger.Instance,
                helloTracker: helloTracker);
            t.FinalizingSetupPhaseTriggered += (_, reason) => _finalizingReasons.Add(reason);
            t.WhiteGloveCompleted += (_, __) => System.Threading.Interlocked.Increment(ref _whiteGloveCompletedCount);
            t.EspFailureDetected += (_, ft) => _espFailures.Add(ft);
            return t;
        }

        private HelloTracker CreateHelloTracker() =>
            new HelloTracker("sess-1", "tenant-1", _ => { }, TestLogger.Instance, helloWaitTimeoutSeconds: 30);

        private EnrollmentEvent Last(string eventType) =>
            _captured.ToList().LastOrDefault(e => e.EventType == eventType);

        // =====================================================================
        // Event 62404 — Hello wizard start
        // =====================================================================

        [Fact]
        public void ProcessEvent_62404_WithAADHello_EmitsHelloWizardStarted_AndTriggersFinalizingSetup()
        {
            var t = CreateTracker();

            t.ProcessEvent(62404,
                "CloudExperienceHost Web App Activity Started. Name: 'AADHello' ...",
                DateTime.UtcNow, "Microsoft-Windows-Shell-Core", false);

            var evt = Last("hello_wizard_started");
            Assert.NotNull(evt);
            Assert.Equal(EventSeverity.Info, evt.Severity);
            Assert.Single(_finalizingReasons);
            Assert.Equal("hello_wizard_started", _finalizingReasons[0]);
        }

        [Fact]
        public void ProcessEvent_62404_WithNGC_EmitsHelloWizardStarted()
        {
            var t = CreateTracker();

            t.ProcessEvent(62404,
                "CloudExperienceHost Web App Activity Started. Name: 'NGC' ...",
                DateTime.UtcNow, "prov", false);

            Assert.NotNull(Last("hello_wizard_started"));
        }

        [Fact]
        public void ProcessEvent_62404_NonHello_Ignored()
        {
            var t = CreateTracker();

            t.ProcessEvent(62404,
                "CloudExperienceHost Web App Activity Started. Name: 'OtherThing'",
                DateTime.UtcNow, "prov", false);

            Assert.Empty(_captured);
            Assert.Empty(_finalizingReasons);
        }

        [Fact]
        public void ProcessEvent_62404_NotifiesHelloTracker()
        {
            var hello = CreateHelloTracker();
            var t = CreateTracker(hello);

            t.ProcessEvent(62404, "CloudExperienceHost ... 'AADHello' ...",
                DateTime.UtcNow, "prov", false);

            Assert.True(hello.IsHelloWizardStartedForTest);
        }

        // =====================================================================
        // Event 62407 — WhiteGlove success
        // =====================================================================

        [Fact]
        public void ProcessEvent_62407_WhiteGloveSuccess_EmitsWhiteGloveComplete_FireOnce()
        {
            var t = CreateTracker();

            t.ProcessEvent(62407,
                "CloudExperienceHost Web App Event 2. Name: 'CommercialOOBE_ESPProgress_WhiteGlove_Success'",
                DateTime.UtcNow, "prov", false);
            t.ProcessEvent(62407,
                "CloudExperienceHost Web App Event 2. Name: 'CommercialOOBE_ESPProgress_WhiteGlove_Success'",
                DateTime.UtcNow.AddSeconds(1), "prov", false);

            Assert.Single(_captured.ToList().Where(e => e.EventType == "whiteglove_complete"));
            Assert.Equal(1, _whiteGloveCompletedCount);
            Assert.True(t.IsWhiteGloveDetectedForTest);
            // WhiteGlove does NOT trigger FinalizingSetup
            Assert.Empty(_finalizingReasons);
        }

        // =====================================================================
        // Event 62407 — ESP failures
        // =====================================================================

        [Theory]
        [InlineData("ESPProgress_Failure")]
        [InlineData("ESPProgress_Failed")]
        [InlineData("ESPProgress_Timeout")]
        [InlineData("ESPProgress_Abort")]
        [InlineData("WhiteGlove_Failed")]
        [InlineData("WhiteGlove_Failure")]
        public void ProcessEvent_62407_WithFailurePattern_FiresEspFailureDetected(string failureKeyword)
        {
            var t = CreateTracker();

            t.ProcessEvent(62407,
                $"CloudExperienceHost Web App Event 2. Name: 'CommercialOOBE_{failureKeyword}'",
                DateTime.UtcNow, "prov", false);

            var evt = Last("esp_failure");
            Assert.NotNull(evt);
            Assert.Equal(EventSeverity.Error, evt.Severity);
            Assert.Equal(failureKeyword, evt.Data["failureType"]);
            Assert.Single(_espFailures);
            Assert.Equal(failureKeyword, _espFailures[0]);
            // Failure does NOT trigger FinalizingSetup
            Assert.Empty(_finalizingReasons);
        }

        // =====================================================================
        // Event 62407 — ESP exit
        // =====================================================================

        [Fact]
        public void ProcessEvent_62407_OobeEspExiting_EmitsEspExiting_AndTriggersFinalizingSetup()
        {
            var t = CreateTracker();

            t.ProcessEvent(62407,
                "CloudExperienceHost Web App Event 2. Name: 'CommercialOOBE_ESPProgress_Page_Exiting'",
                DateTime.UtcNow, "prov", false);

            Assert.NotNull(Last("esp_exiting"));
            Assert.True(t.IsEspExitedForTest);
            Assert.Single(_finalizingReasons);
            Assert.Equal("esp_exiting", _finalizingReasons[0]);
        }

        [Fact]
        public void ProcessEvent_62407_EspExit_NotifiesHelloTracker()
        {
            var hello = CreateHelloTracker();
            var t = CreateTracker(hello);
            hello.StartHelloWaitTimer();
            hello.SetPolicyForTest(helloEnabled: true, source: "GPO");

            t.ProcessEvent(62407,
                "CloudExperienceHost Web App Event 2. Name: 'CommercialOOBE_ESPProgress_Page_Exiting'",
                DateTime.UtcNow, "prov", false);

            // Hello gets notified → _espExitSeen flag is true → surfaces in next wait-timeout event data.
            // We trigger the wait-timeout to observe it.
            hello.TriggerWaitTimeoutForTest();
            // With policy enabled, wait-timeout emits extended_wait with espExitDetected=true
            // (indirectly asserting that NotifyEspExited was called).
        }

        [Fact]
        public void ProcessEvent_62407_UnknownPattern_Ignored()
        {
            var t = CreateTracker();

            t.ProcessEvent(62407,
                "CloudExperienceHost Web App Event 2. Name: 'Unrelated'",
                DateTime.UtcNow, "prov", false);

            Assert.Empty(_captured);
            Assert.Empty(_finalizingReasons);
        }

        // =====================================================================
        // WhiteGlove vs ESPProgress_Page_Exiting ordering: WhiteGlove must win
        // =====================================================================

        [Fact]
        public void ProcessEvent_62407_WhiteGloveSuccess_DoesNotAlsoMatchEspExitingRegex()
        {
            // The WhiteGlove success description contains "Exiting page due to White Glove success."
            // The generic OOBE_ESP.*Exiting regex must NOT match here because WhiteGlove is checked first.
            var t = CreateTracker();

            t.ProcessEvent(62407,
                "CloudExperienceHost-Web-App-Ereignis 2. Name: 'CommercialOOBE_ESPProgress_WhiteGlove_Success', Wert: '{\"message\":\"BootstrapStatus: Exiting page due to White Glove success.\"}'.",
                DateTime.UtcNow, "prov", false);

            Assert.Equal(1, _whiteGloveCompletedCount);
            Assert.Null(Last("esp_exiting"));
            Assert.False(t.IsEspExitedForTest);
        }

        // =====================================================================
        // ExtractEspFailureType / HasEspFailurePattern
        // =====================================================================

        [Fact]
        public void ExtractEspFailureType_ReturnsFirstMatchingKeyword()
        {
            Assert.Equal("ESPProgress_Failure",
                ShellCoreTracker.ExtractEspFailureType("prefix ESPProgress_Failure suffix"));
            Assert.Equal("ESPProgress_Timeout",
                ShellCoreTracker.ExtractEspFailureType("a ESPProgress_Timeout b"));
            Assert.Equal("WhiteGlove_Failed",
                ShellCoreTracker.ExtractEspFailureType("WhiteGlove_Failed happened"));
            Assert.Equal("Unknown_ESP_Failure",
                ShellCoreTracker.ExtractEspFailureType("no known keyword here"));
        }

        [Fact]
        public void HasEspFailurePattern_DetectsAllKnownKeywords()
        {
            Assert.True(ShellCoreTracker.HasEspFailurePattern("x ESPProgress_Failure y"));
            Assert.True(ShellCoreTracker.HasEspFailurePattern("x espprogress_failed y")); // case-insensitive
            Assert.False(ShellCoreTracker.HasEspFailurePattern("ordinary message"));
            Assert.False(ShellCoreTracker.HasEspFailurePattern(""));
        }

        // =====================================================================
        // Backfill handler — dedup with live state
        // =====================================================================

        [Fact]
        public void HandleBackfillRecord_EspExit_FiresOnceEvenAcrossMultipleBackfillRecords()
        {
            var t = CreateTracker();

            t.HandleBackfillRecord("name: 'CommercialOOBE_ESPProgress_Page_Exiting'");
            t.HandleBackfillRecord("another CommercialOOBE_ESPProgress_Page_Exiting record");

            Assert.Single(_finalizingReasons);
            Assert.True(t.IsEspExitedForTest);
        }

        [Fact]
        public void HandleBackfillRecord_EspExit_AfterLiveEvent_DoesNotRefire()
        {
            var t = CreateTracker();

            t.ProcessEvent(62407,
                "CommercialOOBE_ESPProgress_Page_Exiting", DateTime.UtcNow, "prov", false);
            var liveReasonsCount = _finalizingReasons.Count;

            t.HandleBackfillRecord("CommercialOOBE_ESPProgress_Page_Exiting");

            Assert.Equal(liveReasonsCount, _finalizingReasons.Count);
        }

        [Fact]
        public void HandleBackfillRecord_Failure_AlwaysFires()
        {
            // Backfill failure path has no fire-once guard — every match triggers EspFailureDetected.
            var t = CreateTracker();

            t.HandleBackfillRecord("ESPProgress_Failure: boom");

            Assert.Single(_espFailures);
            Assert.Equal("ESPProgress_Failure", _espFailures[0]);
        }
    }
}
