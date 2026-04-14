using System;
using System.IO;
using AutopilotMonitor.Agent.Core.Monitoring.Core;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AutopilotMonitor.Agent.Core.Tests.Core
{
    /// <summary>
    /// Covers the three outcomes (updated / skipped / check_failed / up_to_date), the dedup rules,
    /// and the defensive behaviors (no marker, corrupt JSON, stale markers).
    /// </summary>
    public class VersionCheckEventBuilderTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly VersionCheckEventBuilder.Paths _paths;

        private const string SessionA = "11111111-1111-1111-1111-111111111111";
        private const string SessionB = "22222222-2222-2222-2222-222222222222";
        private const string TenantId = "tenant-xyz";

        public VersionCheckEventBuilderTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "vceb-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _paths = new VersionCheckEventBuilder.Paths
            {
                UpdatedMarker = Path.Combine(_tempDir, "self-update-info.json"),
                SkippedMarker = Path.Combine(_tempDir, "self-update-skipped.json"),
                CheckedMarker = Path.Combine(_tempDir, "self-update-checked.json"),
                LastEmit      = Path.Combine(_tempDir, "last-version-check.json"),
            };
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
        }

        private void WriteChecked(string current = "1.0.949", string latest = "1.0.949", long versionCheckMs = 123)
        {
            var json = new JObject
            {
                ["outcome"]        = "up_to_date",
                ["currentVersion"] = current,
                ["latestVersion"]  = latest,
                ["versionCheckMs"] = versionCheckMs,
                ["checkedAtUtc"]   = DateTime.UtcNow.ToString("O"),
            };
            File.WriteAllText(_paths.CheckedMarker, json.ToString());
        }

        private void WriteUpdated(string previous = "1.0.949", string newVer = "1.0.950", string trigger = "startup")
        {
            var now = DateTime.UtcNow;
            var json = new JObject
            {
                ["outcome"]         = "updated",
                ["previousVersion"] = previous,
                ["newVersion"]      = newVer,
                ["triggerReason"]   = trigger,
                ["updatedAtUtc"]    = now.ToString("O"),
                ["exitAtUtc"]       = now.AddSeconds(-2).ToString("O"),
                ["versionCheckMs"]  = 100L,
                ["downloadMs"]      = 2000L,
                ["zipSizeBytes"]    = 1234567L,
                ["verifyMs"]        = 40L,
                ["extractMs"]       = 200L,
                ["swapMs"]          = 50L,
                ["totalUpdateMs"]   = 2400L,
            };
            File.WriteAllText(_paths.UpdatedMarker, json.ToString());
        }

        private void WriteSkipped(string reason = "download_failed", string latest = "1.0.950", string outcome = null)
        {
            var json = new JObject
            {
                ["reason"]         = reason,
                ["currentVersion"] = "1.0.949",
                ["latestVersion"]  = latest,
                ["skippedAtUtc"]   = DateTime.UtcNow.ToString("O"),
                ["errorDetail"]    = "timeout after 10000ms",
            };
            if (outcome != null) json["outcome"] = outcome;
            File.WriteAllText(_paths.SkippedMarker, json.ToString());
        }

        private void WriteLastEmit(string sessionId, string latestVersion, string outcome)
        {
            var json = new JObject
            {
                ["sessionId"]     = sessionId,
                ["latestVersion"] = latestVersion,
                ["outcome"]       = outcome,
                ["emittedAtUtc"]  = DateTime.UtcNow.ToString("O"),
            };
            File.WriteAllText(_paths.LastEmit, json.ToString());
        }

        [Fact]
        public void NoMarker_ReturnsNoEvent()
        {
            var result = VersionCheckEventBuilder.TryBuild(SessionA, TenantId, DateTime.UtcNow, _paths);

            Assert.Null(result.Event);
            Assert.False(result.Deduped);
            Assert.Null(result.Outcome);
            Assert.Null(result.ParseError);
        }

        [Fact]
        public void UpToDateMarker_FirstTime_EmitsEventAndPersistsLastEmit()
        {
            WriteChecked(latest: "1.0.949");

            var result = VersionCheckEventBuilder.TryBuild(SessionA, TenantId, DateTime.UtcNow, _paths);

            Assert.NotNull(result.Event);
            Assert.Equal(Constants.EventTypes.AgentVersionCheck, result.Event.EventType);
            Assert.Equal(EventSeverity.Info, result.Event.Severity);
            Assert.Equal("up_to_date", result.Event.Data["outcome"]);
            Assert.Equal("1.0.949", result.Event.Data["latestVersion"]);
            Assert.False(File.Exists(_paths.CheckedMarker)); // marker deleted
            Assert.True(File.Exists(_paths.LastEmit));       // last-emit persisted
        }

        [Fact]
        public void UpToDateMarker_SameSessionSameVersion_IsDeduped()
        {
            WriteLastEmit(SessionA, "1.0.949", "up_to_date");
            WriteChecked(latest: "1.0.949");

            var result = VersionCheckEventBuilder.TryBuild(SessionA, TenantId, DateTime.UtcNow, _paths);

            Assert.Null(result.Event);
            Assert.True(result.Deduped);
            Assert.Equal("up_to_date", result.Outcome);
            Assert.False(File.Exists(_paths.CheckedMarker));
        }

        [Fact]
        public void UpToDateMarker_DifferentSession_EmitsEvent()
        {
            WriteLastEmit(SessionA, "1.0.949", "up_to_date");
            WriteChecked(latest: "1.0.949");

            var result = VersionCheckEventBuilder.TryBuild(SessionB, TenantId, DateTime.UtcNow, _paths);

            Assert.NotNull(result.Event);
            Assert.False(result.Deduped);
        }

        [Fact]
        public void UpToDateMarker_SameSessionButNewerLatestVersion_EmitsEvent()
        {
            // White-glove scenario: after a pause a new release appeared. Dedup must break.
            WriteLastEmit(SessionA, "1.0.949", "up_to_date");
            WriteChecked(latest: "1.0.950");

            var result = VersionCheckEventBuilder.TryBuild(SessionA, TenantId, DateTime.UtcNow, _paths);

            Assert.NotNull(result.Event);
            Assert.False(result.Deduped);
            Assert.Equal("1.0.950", result.Event.Data["latestVersion"]);
        }

        [Fact]
        public void UpdatedMarker_AlwaysEmits_IgnoresLastEmit()
        {
            WriteLastEmit(SessionA, "1.0.950", "updated");
            WriteUpdated(previous: "1.0.949", newVer: "1.0.950", trigger: "startup");

            var result = VersionCheckEventBuilder.TryBuild(SessionA, TenantId, DateTime.UtcNow, _paths);

            Assert.NotNull(result.Event);
            Assert.Equal("updated", result.Event.Data["outcome"]);
            Assert.Equal(EventSeverity.Info, result.Event.Severity);
            Assert.Equal("1.0.949", result.Event.Data["previousVersion"]);
            Assert.Equal("1.0.950", result.Event.Data["newVersion"]);
            Assert.True(result.Event.Data.ContainsKey("downtimeMs"));
            Assert.False(File.Exists(_paths.UpdatedMarker));
        }

        [Fact]
        public void UpdatedMarker_RuntimeTrigger_IsWarningSeverity()
        {
            WriteUpdated(trigger: "runtime_hash_mismatch");

            var result = VersionCheckEventBuilder.TryBuild(SessionA, TenantId, DateTime.UtcNow, _paths);

            Assert.Equal(EventSeverity.Warning, result.Event.Severity);
        }

        [Fact]
        public void SkippedMarker_DownloadFailed_EmitsWithSkippedOutcome()
        {
            WriteSkipped(reason: "download_failed");

            var result = VersionCheckEventBuilder.TryBuild(SessionA, TenantId, DateTime.UtcNow, _paths);

            Assert.NotNull(result.Event);
            Assert.Equal("skipped", result.Event.Data["outcome"]);
            Assert.Equal("download_failed", result.Event.Data["reason"]);
            Assert.Equal(EventSeverity.Warning, result.Event.Severity);
            Assert.False(File.Exists(_paths.SkippedMarker));
        }

        [Fact]
        public void SkippedMarker_VersionCheckFailed_DerivesCheckFailedOutcome()
        {
            // Backward-compat: old marker without outcome field — must still map correctly.
            WriteSkipped(reason: "version_check_failed", latest: null, outcome: null);

            var result = VersionCheckEventBuilder.TryBuild(SessionA, TenantId, DateTime.UtcNow, _paths);

            Assert.Equal("check_failed", result.Event.Data["outcome"]);
        }

        [Fact]
        public void Priority_UpdatedBeatsSkippedBeatsChecked()
        {
            // All three present (edge case after a crash). Updated wins, the others are cleaned up.
            WriteUpdated();
            WriteSkipped();
            WriteChecked();

            var result = VersionCheckEventBuilder.TryBuild(SessionA, TenantId, DateTime.UtcNow, _paths);

            Assert.Equal("updated", result.Event.Data["outcome"]);
            Assert.False(File.Exists(_paths.UpdatedMarker));
            Assert.False(File.Exists(_paths.SkippedMarker));
            Assert.False(File.Exists(_paths.CheckedMarker));
        }

        [Fact]
        public void CorruptMarker_DoesNotThrow_ReturnsParseError()
        {
            File.WriteAllText(_paths.CheckedMarker, "{ not valid json");

            var result = VersionCheckEventBuilder.TryBuild(SessionA, TenantId, DateTime.UtcNow, _paths);

            Assert.Null(result.Event);
            Assert.NotNull(result.ParseError);
            Assert.False(File.Exists(_paths.CheckedMarker)); // still cleaned up
        }
    }
}
