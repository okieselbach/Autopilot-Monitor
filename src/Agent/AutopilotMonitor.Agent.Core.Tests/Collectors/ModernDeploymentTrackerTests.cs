using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using AutopilotMonitor.Agent.Core.Tests.Helpers;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;
using Xunit;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Flows;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Completion;

namespace AutopilotMonitor.Agent.Core.Tests.Collectors
{
    /// <summary>
    /// Tests for <see cref="ModernDeploymentTracker"/>. Drives the ProcessEvent pipeline via
    /// primitive inputs (EventRecord is abstract and Windows-only, so tests don't synthesise it).
    /// Covers: level → event-type mapping, harmless-warning downgrade, WhiteGlove fire-once,
    /// WhiteGlove keyword guard, cross-restart dedup via persisted state.
    /// </summary>
    public sealed class ModernDeploymentTrackerTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly ConcurrentBag<EnrollmentEvent> _captured;

        public ModernDeploymentTrackerTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "mdt-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _captured = new ConcurrentBag<EnrollmentEvent>();
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
            catch { /* best-effort */ }
        }

        private ModernDeploymentTracker CreateTracker(string stateDirectory = null, int[] harmlessEventIds = null)
        {
            return new ModernDeploymentTracker(
                sessionId: "sess-1",
                tenantId: "tenant-1",
                onEventCollected: e => _captured.Add(e),
                logger: TestLogger.Instance,
                logLevelMax: 3,
                backfillEnabled: true,
                backfillLookbackMinutes: 30,
                stateDirectory: stateDirectory,
                harmlessEventIds: harmlessEventIds);
        }

        private List<EnrollmentEvent> GetCaptured() => new List<EnrollmentEvent>(_captured);

        // ========== Level → EventType mapping ==========

        [Fact]
        public void ProcessEvent_Level1_MappedToError()
        {
            var tracker = CreateTracker();

            tracker.ProcessEvent(
                eventId: 42, level: 1, levelDisplayName: "Critical", providerName: "prov",
                timeCreatedUtc: DateTime.UtcNow, formattedDescription: "Boom",
                shortName: "Autopilot", channelName: ModernDeploymentTracker.AutopilotChannel,
                isBackfill: false);

            var evt = Assert.Single(GetCaptured());
            Assert.Equal(Constants.EventTypes.ModernDeploymentError, evt.EventType);
            Assert.Equal(EventSeverity.Error, evt.Severity);
            Assert.Equal("ModernDeploymentWatcher", evt.Source);
            Assert.Equal(EnrollmentPhase.Unknown, evt.Phase);
        }

        [Fact]
        public void ProcessEvent_Level2_MappedToError()
        {
            var tracker = CreateTracker();

            tracker.ProcessEvent(42, 2, "Error", "prov", DateTime.UtcNow, "msg",
                "Autopilot", ModernDeploymentTracker.AutopilotChannel, false);

            Assert.Equal(Constants.EventTypes.ModernDeploymentError, Assert.Single(GetCaptured()).EventType);
        }

        [Fact]
        public void ProcessEvent_Level3_MappedToWarning()
        {
            var tracker = CreateTracker();

            tracker.ProcessEvent(42, 3, "Warning", "prov", DateTime.UtcNow, "msg",
                "Autopilot", ModernDeploymentTracker.AutopilotChannel, false);

            var evt = Assert.Single(GetCaptured());
            Assert.Equal(Constants.EventTypes.ModernDeploymentWarning, evt.EventType);
            Assert.Equal(EventSeverity.Warning, evt.Severity);
        }

        [Fact]
        public void ProcessEvent_Level4_MappedToInfoLog()
        {
            var tracker = CreateTracker();

            tracker.ProcessEvent(42, 4, "Information", "prov", DateTime.UtcNow, "msg",
                "Autopilot", ModernDeploymentTracker.AutopilotChannel, false);

            var evt = Assert.Single(GetCaptured());
            Assert.Equal(Constants.EventTypes.ModernDeploymentLog, evt.EventType);
            Assert.Equal(EventSeverity.Info, evt.Severity);
        }

        [Fact]
        public void ProcessEvent_NullLevel_DefaultsToInfoLog()
        {
            var tracker = CreateTracker();

            tracker.ProcessEvent(42, null, null, null, DateTime.UtcNow, "msg",
                "Autopilot", ModernDeploymentTracker.AutopilotChannel, false);

            Assert.Equal(Constants.EventTypes.ModernDeploymentLog, Assert.Single(GetCaptured()).EventType);
        }

        // ========== Harmless warning downgrade (Event 100, Level 3) ==========

        [Fact]
        public void ProcessEvent_EventId100Level3_DowngradedToDebugLog()
        {
            var tracker = CreateTracker();

            tracker.ProcessEvent(100, 3, "Warning", "prov", DateTime.UtcNow,
                "Autopilot policy not found",
                "Autopilot", ModernDeploymentTracker.AutopilotChannel, false);

            var evt = Assert.Single(GetCaptured());
            Assert.Equal(Constants.EventTypes.ModernDeploymentLog, evt.EventType);
            Assert.Equal(EventSeverity.Debug, evt.Severity);
        }

        [Fact]
        public void ProcessEvent_EventId100Level2_DowngradedToDebugLog()
        {
            var tracker = CreateTracker();

            tracker.ProcessEvent(100, 2, "Error", "prov", DateTime.UtcNow, "msg",
                "Autopilot", ModernDeploymentTracker.AutopilotChannel, false);

            var evt = Assert.Single(GetCaptured());
            Assert.Equal(Constants.EventTypes.ModernDeploymentLog, evt.EventType);
            Assert.Equal(EventSeverity.Debug, evt.Severity);
        }

        [Fact]
        public void ProcessEvent_EventId100Level1Critical_NeverDowngraded()
        {
            var tracker = CreateTracker();

            tracker.ProcessEvent(100, 1, "Critical", "prov", DateTime.UtcNow, "msg",
                "Autopilot", ModernDeploymentTracker.AutopilotChannel, false);

            var evt = Assert.Single(GetCaptured());
            Assert.Equal(Constants.EventTypes.ModernDeploymentError, evt.EventType);
            Assert.Equal(EventSeverity.Error, evt.Severity);
        }

        [Fact]
        public void ProcessEvent_DefaultHarmlessList_IncludesEventId1005()
        {
            var tracker = CreateTracker();

            tracker.ProcessEvent(1005, 3, "Warning", "prov", DateTime.UtcNow, "noisy",
                "Autopilot", ModernDeploymentTracker.AutopilotChannel, false);

            var evt = Assert.Single(GetCaptured());
            Assert.Equal(Constants.EventTypes.ModernDeploymentLog, evt.EventType);
            Assert.Equal(EventSeverity.Debug, evt.Severity);
        }

        [Fact]
        public void ProcessEvent_CustomHarmlessList_OverridesDefaults()
        {
            var tracker = CreateTracker(harmlessEventIds: new[] { 4242 });

            // 100 is NOT in the custom list → stays Warning
            tracker.ProcessEvent(100, 3, "Warning", "prov", DateTime.UtcNow, "msg",
                "Autopilot", ModernDeploymentTracker.AutopilotChannel, false);
            // 4242 IS in the custom list → downgraded
            tracker.ProcessEvent(4242, 3, "Warning", "prov", DateTime.UtcNow, "msg",
                "Autopilot", ModernDeploymentTracker.AutopilotChannel, false);

            var events = GetCaptured();
            Assert.Equal(2, events.Count);
            Assert.Contains(events, e => e.Data != null && (int)e.Data["eventId"] == 100 && e.Severity == EventSeverity.Warning);
            Assert.Contains(events, e => e.Data != null && (int)e.Data["eventId"] == 4242 && e.Severity == EventSeverity.Debug);
        }

        [Fact]
        public void ProcessEvent_UnlistedHighLevelEvent_RemainsAsError()
        {
            var tracker = CreateTracker();

            // 999 not in the default harmless list → Level 2 stays Error
            tracker.ProcessEvent(999, 2, "Error", "prov", DateTime.UtcNow, "msg",
                "Autopilot", ModernDeploymentTracker.AutopilotChannel, false);

            var evt = Assert.Single(GetCaptured());
            Assert.Equal(Constants.EventTypes.ModernDeploymentError, evt.EventType);
            Assert.Equal(EventSeverity.Error, evt.Severity);
        }

        // ========== Message payload shape ==========

        [Fact]
        public void ProcessEvent_EmptyDescription_UsesFallback()
        {
            var tracker = CreateTracker();

            tracker.ProcessEvent(999, 4, "Information", "prov", DateTime.UtcNow, null,
                "Autopilot", ModernDeploymentTracker.AutopilotChannel, false);

            var evt = Assert.Single(GetCaptured());
            Assert.Contains("EventID 999", evt.Message);
            Assert.Contains("no formatted description", evt.Message);
        }

        [Fact]
        public void ProcessEvent_LongDescription_TruncatedTo1000Chars()
        {
            var tracker = CreateTracker();
            var longDesc = new string('x', 1500);

            tracker.ProcessEvent(42, 4, "Information", "prov", DateTime.UtcNow, longDesc,
                "Autopilot", ModernDeploymentTracker.AutopilotChannel, false);

            var evt = Assert.Single(GetCaptured());
            // Message prefix "[Autopilot] EventID 42: " + 1000 chars + "…"
            Assert.True(evt.Message.Length <= 2000);
            Assert.EndsWith("…", evt.Message);
        }

        [Fact]
        public void ProcessEvent_DataDictionaryContainsAllExpectedKeys()
        {
            var tracker = CreateTracker();
            var ts = new DateTime(2026, 4, 14, 10, 0, 0, DateTimeKind.Utc);

            tracker.ProcessEvent(42, 3, "Warning", "MyProvider", ts, "hello",
                "ManagementService", ModernDeploymentTracker.ManagementChannel, isBackfill: true);

            var evt = Assert.Single(GetCaptured());
            Assert.Equal("ManagementService", evt.Data["channel"]);
            Assert.Equal(ModernDeploymentTracker.ManagementChannel, evt.Data["channelFullName"]);
            Assert.Equal(42, evt.Data["eventId"]);
            Assert.Equal(3, evt.Data["level"]);
            Assert.Equal("Warning", evt.Data["levelName"]);
            Assert.Equal("MyProvider", evt.Data["providerName"]);
            Assert.Equal(true, evt.Data["backfilled"]);
        }

        // ========== WhiteGlove Event 509 handling ==========

        [Fact]
        public void ProcessEvent_WhiteGloveStart_FromManagementService_EmitsWhiteGloveStartedEvent()
        {
            var tracker = CreateTracker(_tempDir);

            tracker.ProcessEvent(
                ModernDeploymentTracker.EventId_WhiteGloveStart, 4, "Information", "prov",
                DateTime.UtcNow,
                "AutopilotManager enabled TPM requirement due to WhiteGlove policy value 1",
                "ManagementService", ModernDeploymentTracker.ManagementChannel, isBackfill: false);

            var evt = Assert.Single(GetCaptured());
            Assert.Equal("whiteglove_started", evt.EventType);
            Assert.Equal(EventSeverity.Info, evt.Severity);
            Assert.True(evt.ImmediateUpload);
            Assert.True(tracker.IsWhiteGloveStartDetected);
        }

        [Fact]
        public void ProcessEvent_WhiteGloveStart_FromAutopilotChannel_TreatedAsRegularLog()
        {
            // Event 509 is only treated as WhiteGlove when it comes from ManagementService.
            // The Autopilot channel may emit event 509 for other purposes → fall through to
            // the generic level-based mapping.
            var tracker = CreateTracker(_tempDir);

            tracker.ProcessEvent(
                ModernDeploymentTracker.EventId_WhiteGloveStart, 4, "Information", "prov",
                DateTime.UtcNow, "AutopilotManager WhiteGlove whatever",
                "Autopilot", ModernDeploymentTracker.AutopilotChannel, isBackfill: false);

            var evt = Assert.Single(GetCaptured());
            Assert.Equal(Constants.EventTypes.ModernDeploymentLog, evt.EventType);
            Assert.False(tracker.IsWhiteGloveStartDetected);
        }

        [Fact]
        public void ProcessEvent_WhiteGloveEvent509_WithoutWhiteGloveKeyword_Ignored()
        {
            // Guard against future reuse of Event 509 for unrelated purposes.
            var tracker = CreateTracker(_tempDir);

            tracker.ProcessEvent(
                ModernDeploymentTracker.EventId_WhiteGloveStart, 4, "Information", "prov",
                DateTime.UtcNow, "Some unrelated message",
                "ManagementService", ModernDeploymentTracker.ManagementChannel, isBackfill: false);

            Assert.Empty(GetCaptured());
            Assert.False(tracker.IsWhiteGloveStartDetected);
        }

        [Fact]
        public void ProcessEvent_WhiteGloveStart_FireOnce_DuplicatesSuppressed()
        {
            var tracker = CreateTracker(_tempDir);
            var desc = "AutopilotManager enabled TPM requirement due to WhiteGlove policy value 1";

            tracker.ProcessEvent(
                ModernDeploymentTracker.EventId_WhiteGloveStart, 4, "Information", "prov",
                DateTime.UtcNow, desc,
                "ManagementService", ModernDeploymentTracker.ManagementChannel, false);
            tracker.ProcessEvent(
                ModernDeploymentTracker.EventId_WhiteGloveStart, 4, "Information", "prov",
                DateTime.UtcNow.AddSeconds(5), desc,
                "ManagementService", ModernDeploymentTracker.ManagementChannel, false);

            Assert.Single(GetCaptured());
        }

        [Fact]
        public void ProcessEvent_WhiteGloveStart_PersistsStateToDisk()
        {
            var tracker = CreateTracker(_tempDir);
            var eventTime = new DateTime(2026, 4, 14, 9, 30, 0, DateTimeKind.Utc);

            tracker.ProcessEvent(
                ModernDeploymentTracker.EventId_WhiteGloveStart, 4, "Information", "prov",
                eventTime,
                "AutopilotManager enabled TPM requirement due to WhiteGlove policy value 1",
                "ManagementService", ModernDeploymentTracker.ManagementChannel, false);

            var statePath = Path.Combine(_tempDir, ModernDeploymentTracker.WhiteGloveBackfillStateFileName);
            Assert.True(File.Exists(statePath));
            var state = JsonConvert.DeserializeObject<ModernDeploymentTracker.WhiteGloveBackfillState>(File.ReadAllText(statePath));
            Assert.True(state.WhiteGloveStartSeen);
            Assert.Equal(eventTime, state.SeenUtc);
        }

        [Fact]
        public void LoadWhiteGloveBackfillState_NoStateDirectory_ReturnsNull()
        {
            var tracker = CreateTracker(stateDirectory: null);
            Assert.Null(tracker.LoadWhiteGloveBackfillState());
        }

        [Fact]
        public void LoadWhiteGloveBackfillState_NoFile_ReturnsNull()
        {
            var tracker = CreateTracker(_tempDir);
            Assert.Null(tracker.LoadWhiteGloveBackfillState());
        }

        [Fact]
        public void LoadWhiteGloveBackfillState_CorruptFile_ReturnsNullAndDoesNotThrow()
        {
            var tracker = CreateTracker(_tempDir);
            var statePath = Path.Combine(_tempDir, ModernDeploymentTracker.WhiteGloveBackfillStateFileName);
            File.WriteAllText(statePath, "{not valid json");

            Assert.Null(tracker.LoadWhiteGloveBackfillState());
        }

        [Fact]
        public void LoadWhiteGloveBackfillState_RoundTrip_PreservesFlagAndTimestamps()
        {
            var tracker = CreateTracker(_tempDir);
            var eventTime = new DateTime(2026, 4, 14, 9, 30, 0, DateTimeKind.Utc);

            tracker.PersistWhiteGloveBackfillState(eventTime);
            var loaded = tracker.LoadWhiteGloveBackfillState();

            Assert.NotNull(loaded);
            Assert.True(loaded.WhiteGloveStartSeen);
            Assert.Equal(eventTime, loaded.SeenUtc);
        }

        // ========== XPath builder ==========

        [Fact]
        public void BuildXPath_NoTargetedIds_UsesLevelFilterOnly()
        {
            var xpath = ModernDeploymentTracker.BuildXPath(3, null);
            Assert.Contains("Level=0 or (Level >= 1 and Level <= 3)", xpath);
            Assert.DoesNotContain("EventID=", xpath);
        }

        [Fact]
        public void BuildXPath_WithTargetedIds_IncludesOrClause()
        {
            var xpath = ModernDeploymentTracker.BuildXPath(3, new HashSet<int> { 509 });
            Assert.Contains("Level=0 or (Level >= 1 and Level <= 3)", xpath);
            Assert.Contains("EventID=509", xpath);
        }

        [Fact]
        public void BuildXPath_LevelClampedToWindowsRange()
        {
            Assert.Contains("Level <= 1", ModernDeploymentTracker.BuildXPath(0, null));
            Assert.Contains("Level <= 5", ModernDeploymentTracker.BuildXPath(99, null));
        }
    }
}
