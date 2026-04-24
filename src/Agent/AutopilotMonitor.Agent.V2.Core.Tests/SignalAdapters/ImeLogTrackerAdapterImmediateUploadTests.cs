using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared.Models;
using SharedEventTypes = AutopilotMonitor.Shared.Constants.EventTypes;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.SignalAdapters
{
    /// <summary>
    /// First-session fix PR 2 / Fix 1: assert <c>ImmediateUpload=true</c> on live lifecycle
    /// events so UI/MCP see them within seconds — including <c>download_progress</c>, which
    /// drives the live download bar in the UI (bounded ~every 3s, only during active download).
    /// Non-user-facing / non-lifecycle events
    /// (<c>ime_user_session_completed</c>, <c>ime_agent_version</c>, <c>script_completed</c>)
    /// stay batched so background telemetry remains cost-efficient.
    /// </summary>
    public sealed class ImeLogTrackerAdapterImmediateUploadTests
    {

        [Fact]
        public void EspPhaseChanged_info_event_is_marked_immediate_upload()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerEspPhaseFromTest("DeviceSetup");

            var info = f.InfoEvent(SharedEventTypes.EspPhaseChanged);
            Assert.Equal("true", info.Payload![SignalPayloadKeys.ImmediateUpload]);
        }

        [Theory]
        [InlineData(AppInstallationState.Installing, SharedEventTypes.AppInstallStart, "true")]
        [InlineData(AppInstallationState.InProgress, SharedEventTypes.AppInstallStart, "true")]
        [InlineData(AppInstallationState.Installed, SharedEventTypes.AppInstallComplete, "true")]
        [InlineData(AppInstallationState.Skipped, SharedEventTypes.AppInstallComplete, "true")]
        [InlineData(AppInstallationState.Postponed, SharedEventTypes.AppInstallComplete, "true")]
        [InlineData(AppInstallationState.Error, SharedEventTypes.AppInstallFailed, "true")]
        public void AppState_lifecycle_events_are_marked_immediate_upload(
            AppInstallationState newState, string expectedEventType, string expectedImmediate)
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            var app = new AppPackageState($"app-{newState}", listPos: 0);

            adapter.TriggerAppStateFromTest(app, AppInstallationState.Unknown, newState);

            var info = f.InfoEvent(expectedEventType);
            Assert.Equal(expectedImmediate, info.Payload![SignalPayloadKeys.ImmediateUpload]);
        }

        [Fact]
        public void AppDownloadStarted_is_marked_immediate_upload()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            var app = new AppPackageState("app-a", 0);

            adapter.TriggerAppStateFromTest(app, AppInstallationState.Unknown, AppInstallationState.Downloading);

            var info = f.InfoEvent(SharedEventTypes.AppDownloadStarted);
            Assert.Equal("true", info.Payload![SignalPayloadKeys.ImmediateUpload]);
        }

        [Fact]
        public void DownloadProgress_is_marked_immediate_upload_for_live_ui_bar()
        {
            // Progress ticks run every ~3s and only during an active download (bounded by
            // download duration, not polling). Live UI bar > small extra request volume.
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            var app = new AppPackageState("app-a", 0);

            adapter.TriggerAppStateFromTest(app, AppInstallationState.Downloading, AppInstallationState.Downloading);

            var info = f.InfoEvent(SharedEventTypes.DownloadProgress);
            Assert.Equal("true", info.Payload![SignalPayloadKeys.ImmediateUpload]);
        }

        [Fact]
        public void DoTelemetry_is_marked_immediate_upload()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            var app = new AppPackageState("app-do", 0);

            adapter.TriggerDoTelemetryFromTest(app);

            var info = f.InfoEvent(SharedEventTypes.DoTelemetry);
            Assert.Equal("true", info.Payload![SignalPayloadKeys.ImmediateUpload]);
        }

        [Fact]
        public void ImeUserSessionCompleted_stays_batched()
        {
            // Not a hot lifecycle event for UI gating — keep batched per Fix 1 scope.
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerUserSessionCompletedFromTest();

            var info = f.InfoEvent(SharedEventTypes.ImeUserSessionCompleted);
            Assert.Equal("false", info.Payload![SignalPayloadKeys.ImmediateUpload]);
        }

        [Fact]
        public void ImeAgentVersion_stays_batched()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerImeAgentVersionFromTest("1.101.109.0");

            var info = f.InfoEvent(SharedEventTypes.ImeAgentVersion);
            Assert.Equal("false", info.Payload![SignalPayloadKeys.ImmediateUpload]);
        }

        [Fact]
        public void ScriptCompleted_stays_batched()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerScriptCompletedFromTest(new ScriptExecutionState
            {
                PolicyId = "p-1",
                ScriptType = "platform",
                ExitCode = 0,
                Result = "Success",
            });

            var info = f.InfoEvent(SharedEventTypes.ScriptCompleted);
            Assert.Equal("false", info.Payload![SignalPayloadKeys.ImmediateUpload]);
        }
    }
}
