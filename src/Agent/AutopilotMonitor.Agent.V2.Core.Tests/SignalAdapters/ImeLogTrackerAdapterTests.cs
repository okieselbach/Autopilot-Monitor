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
    public sealed class ImeLogTrackerAdapterTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        private sealed class Fixture : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public AgentLogger Logger { get; }
            public ImeLogTracker Tracker { get; }
            public FakeSignalIngressSink Ingress { get; } = new FakeSignalIngressSink();
            public VirtualClock Clock { get; } = new VirtualClock(Fixed);

            public Fixture()
            {
                Logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
                Tracker = new ImeLogTracker(
                    logFolder: Tmp.Path,
                    patterns: new List<ImeLogPattern>(),
                    logger: Logger);
            }

            public IReadOnlyList<FakeSignalIngressSink.PostedSignal> DecisionSignals(DecisionSignalKind kind) =>
                Ingress.Posted.Where(p => p.Kind == kind).ToList();

            public IReadOnlyList<FakeSignalIngressSink.PostedSignal> InfoEvents(string eventType) =>
                Ingress.Posted
                    .Where(p => p.Kind == DecisionSignalKind.InformationalEvent
                                && p.Payload != null
                                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                                && et == eventType)
                    .ToList();

            public IReadOnlyList<FakeSignalIngressSink.PostedSignal> AllInfoEvents() =>
                Ingress.Posted.Where(p => p.Kind == DecisionSignalKind.InformationalEvent).ToList();

            public IReadOnlyList<FakeSignalIngressSink.PostedSignal> NonInfoSignals() =>
                Ingress.Posted.Where(p => p.Kind != DecisionSignalKind.InformationalEvent).ToList();

            public void Dispose()
            {
                Tracker.Dispose();
                Tmp.Dispose();
            }
        }

        [Fact]
        public void EspPhaseChanged_first_phase_emits_decision_signal_with_phase_in_payload()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerEspPhaseFromTest("DeviceSetup");

            var decisionPost = Assert.Single(f.DecisionSignals(DecisionSignalKind.EspPhaseChanged));
            Assert.Equal("ImeLogTracker", decisionPost.SourceOrigin);
            Assert.Equal("DeviceSetup", decisionPost.Payload![SignalPayloadKeys.EspPhase]);
        }

        [Fact]
        public void EspPhaseChanged_first_phase_also_emits_esp_phase_changed_informational_event()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerEspPhaseFromTest("DeviceSetup");

            var info = Assert.Single(f.InfoEvents(SharedEventTypes.EspPhaseChanged));
            Assert.Equal("ImeLogTracker", info.Payload![SignalPayloadKeys.Source]);
            Assert.Equal("DeviceSetup", info.Payload["espPhase"]);
            // Phase-declaration event — carries non-Unknown EnrollmentPhase.
            Assert.Equal(EnrollmentPhase.DeviceSetup.ToString(), info.Payload["phase"]);
        }

        [Fact]
        public void EspPhaseChanged_AccountSetup_maps_phase_to_AccountSetup_enum()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerEspPhaseFromTest("AccountSetup");

            var info = Assert.Single(f.InfoEvents(SharedEventTypes.EspPhaseChanged));
            Assert.Equal(EnrollmentPhase.AccountSetup.ToString(), info.Payload!["phase"]);
        }

        [Fact]
        public void EspPhaseChanged_unknown_phase_omits_phase_override()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerEspPhaseFromTest("SomethingElse");

            var info = Assert.Single(f.InfoEvents(SharedEventTypes.EspPhaseChanged));
            // Unknown mapping → no phase override, emitter will default to EnrollmentPhase.Unknown.
            Assert.False(info.Payload!.ContainsKey("phase"));
            Assert.Equal("SomethingElse", info.Payload["espPhase"]);
        }

        [Fact]
        public void EspPhaseChanged_same_phase_repeated_is_deduped_for_both_rails()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerEspPhaseFromTest("DeviceSetup");
            adapter.TriggerEspPhaseFromTest("DeviceSetup");
            adapter.TriggerEspPhaseFromTest("DeviceSetup");

            Assert.Single(f.DecisionSignals(DecisionSignalKind.EspPhaseChanged));
            Assert.Single(f.InfoEvents(SharedEventTypes.EspPhaseChanged));
        }

        [Fact]
        public void EspPhaseChanged_distinct_phases_emit_separate_signals_and_info_events()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerEspPhaseFromTest("DeviceSetup");
            adapter.TriggerEspPhaseFromTest("AccountSetup");
            adapter.TriggerEspPhaseFromTest("AccountSetup");  // dedup
            adapter.TriggerEspPhaseFromTest("FinalizingSetup");

            var decisions = f.DecisionSignals(DecisionSignalKind.EspPhaseChanged);
            Assert.Equal(3, decisions.Count);
            Assert.Equal("DeviceSetup", decisions[0].Payload![SignalPayloadKeys.EspPhase]);
            Assert.Equal("AccountSetup", decisions[1].Payload![SignalPayloadKeys.EspPhase]);
            Assert.Equal("FinalizingSetup", decisions[2].Payload![SignalPayloadKeys.EspPhase]);

            var infos = f.InfoEvents(SharedEventTypes.EspPhaseChanged);
            Assert.Equal(3, infos.Count);
        }

        [Fact]
        public void EspPhaseChanged_null_or_empty_phase_is_skipped()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerEspPhaseFromTest(null!);
            adapter.TriggerEspPhaseFromTest("");

            Assert.Empty(f.Ingress.Posted);
        }

        [Fact]
        public void UserSessionCompleted_emits_decision_signal_fire_once()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerUserSessionCompletedFromTest();
            adapter.TriggerUserSessionCompletedFromTest();

            Assert.Single(f.DecisionSignals(DecisionSignalKind.ImeUserSessionCompleted));
        }

        [Fact]
        public void UserSessionCompleted_also_emits_ime_user_session_completed_info_event()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerUserSessionCompletedFromTest();

            var info = Assert.Single(f.InfoEvents(SharedEventTypes.ImeUserSessionCompleted));
            Assert.Equal("ImeLogTracker", info.Payload![SignalPayloadKeys.Source]);
            Assert.True(info.Payload.ContainsKey("detectedAt"));
        }

        [Theory]
        [InlineData(AppInstallationState.Installed, DecisionSignalKind.AppInstallCompleted)]
        [InlineData(AppInstallationState.Skipped, DecisionSignalKind.AppInstallCompleted)]
        [InlineData(AppInstallationState.Postponed, DecisionSignalKind.AppInstallCompleted)]
        [InlineData(AppInstallationState.Error, DecisionSignalKind.AppInstallFailed)]
        public void AppStateChange_to_terminal_state_emits_correct_decision_signal_kind(
            AppInstallationState newState, DecisionSignalKind expectedKind)
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            var app = new AppPackageState($"app-{newState}", listPos: 0);

            adapter.TriggerAppStateFromTest(app, AppInstallationState.Installing, newState);

            var posted = Assert.Single(f.DecisionSignals(expectedKind));
            Assert.Equal($"app-{newState}", posted.Payload!["appId"]);
            Assert.Equal(newState.ToString(), posted.Payload["newState"]);
        }

        [Theory]
        [InlineData(AppInstallationState.Installed, "app_install_completed")]
        [InlineData(AppInstallationState.Skipped, "app_install_completed")]
        [InlineData(AppInstallationState.Postponed, "app_install_completed")]
        [InlineData(AppInstallationState.Error, "app_install_failed")]
        [InlineData(AppInstallationState.Installing, "app_install_started")]
        [InlineData(AppInstallationState.InProgress, "app_install_started")]
        public void AppStateChange_transition_emits_matching_informational_event_type(
            AppInstallationState newState, string expectedEventType)
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            var app = new AppPackageState($"app-{newState}", listPos: 0);

            adapter.TriggerAppStateFromTest(app, AppInstallationState.Unknown, newState);

            var info = Assert.Single(f.InfoEvents(expectedEventType));
            Assert.Equal($"app-{newState}", info.Payload!["appId"]);
            Assert.Equal(newState.ToString(), info.Payload["state"]);
        }

        [Fact]
        public void AppStateChange_into_Downloading_emits_app_download_started()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            var app = new AppPackageState("app-a", 0);

            adapter.TriggerAppStateFromTest(app, AppInstallationState.Unknown, AppInstallationState.Downloading);

            var info = Assert.Single(f.InfoEvents(SharedEventTypes.AppDownloadStarted));
            Assert.Equal("app-a", info.Payload!["appId"]);
            Assert.Equal(EventSeverity.Info.ToString(), info.Payload[SignalPayloadKeys.Severity]);
        }

        [Fact]
        public void AppStateChange_Downloading_to_Downloading_emits_download_progress_debug()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            var app = new AppPackageState("app-a", 0);

            adapter.TriggerAppStateFromTest(app, AppInstallationState.Downloading, AppInstallationState.Downloading);

            var info = Assert.Single(f.InfoEvents(SharedEventTypes.DownloadProgress));
            Assert.Equal(EventSeverity.Debug.ToString(), info.Payload![SignalPayloadKeys.Severity]);
        }

        [Fact]
        public void AppStateChange_to_Unknown_or_NotInstalled_emits_nothing()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            var app = new AppPackageState("app-x", 0);

            adapter.TriggerAppStateFromTest(app, AppInstallationState.Unknown, AppInstallationState.Unknown);
            adapter.TriggerAppStateFromTest(app, AppInstallationState.Unknown, AppInstallationState.NotInstalled);

            Assert.Empty(f.Ingress.Posted);
        }

        [Fact]
        public void AppStateChange_terminal_decision_signal_is_fire_once_per_app()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            var app = new AppPackageState("app-1", 0);

            adapter.TriggerAppStateFromTest(app, AppInstallationState.Installing, AppInstallationState.Installed);
            // Even if the tracker re-fires (shouldn't happen, but defend) — decision signal stays at one.
            adapter.TriggerAppStateFromTest(app, AppInstallationState.Installed, AppInstallationState.Error);

            var completions = f.DecisionSignals(DecisionSignalKind.AppInstallCompleted);
            var failures = f.DecisionSignals(DecisionSignalKind.AppInstallFailed);
            Assert.Single(completions);
            Assert.Empty(failures);
        }

        [Fact]
        public void AppStateChange_different_apps_emit_independent_decision_signals()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerAppStateFromTest(new AppPackageState("a", 0), AppInstallationState.Installing, AppInstallationState.Installed);
            adapter.TriggerAppStateFromTest(new AppPackageState("b", 1), AppInstallationState.Installing, AppInstallationState.Error);
            adapter.TriggerAppStateFromTest(new AppPackageState("c", 2), AppInstallationState.Installing, AppInstallationState.Skipped);

            var completions = f.DecisionSignals(DecisionSignalKind.AppInstallCompleted);
            var failures = f.DecisionSignals(DecisionSignalKind.AppInstallFailed);
            Assert.Equal(2, completions.Count);
            Assert.Single(failures);
            Assert.Contains(completions, p => p.Payload!["appId"] == "a");
            Assert.Contains(completions, p => p.Payload!["appId"] == "c");
            Assert.Equal("b", failures[0].Payload!["appId"]);
        }

        [Fact]
        public void AppStateChange_null_app_is_ignored()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerAppStateFromTest(null!, AppInstallationState.Installing, AppInstallationState.Installed);

            Assert.Empty(f.Ingress.Posted);
        }

        [Fact]
        public void AppStateChange_payload_carries_V1_compatible_fields()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            // Build a fully-populated package via Restore to exercise all optional fields.
            var app = AppPackageState.Restore(
                id: "app-xyz",
                listPos: 0,
                name: "Company Portal",
                runAs: AppRunAs.System,
                intent: AppIntent.Install,
                targeted: AppTargeted.Device,
                dependsOn: new HashSet<string>(),
                installationState: AppInstallationState.Installed,
                downloadingOrInstallingSeen: true,
                progressPercent: 100,
                bytesDownloaded: 100,
                bytesTotal: 100,
                errorPatternId: null,
                errorDetail: null,
                errorCode: null,
                exitCode: null,
                hresultFromWin32: null,
                appVersion: "11.2.1787.0",
                appType: "WinGet",
                attemptNumber: 1,
                detectionResult: "Detected");

            adapter.TriggerAppStateFromTest(app, AppInstallationState.Installing, AppInstallationState.Installed);

            var info = Assert.Single(f.InfoEvents(SharedEventTypes.AppInstallComplete));
            Assert.Equal("app-xyz", info.Payload!["appId"]);
            Assert.Equal("Company Portal", info.Payload["appName"]);
            Assert.Equal("Installed", info.Payload["state"]);
            Assert.Equal("Install", info.Payload["intent"]);
            Assert.Equal("Device", info.Payload["targeted"]);
            Assert.Equal("System", info.Payload["runAs"]);
            Assert.Equal("100", info.Payload["progressPercent"]);
            Assert.Equal("100", info.Payload["bytesDownloaded"]);
            Assert.Equal("100", info.Payload["bytesTotal"]);
            Assert.Equal("false", info.Payload["isError"]);
            Assert.Equal("true", info.Payload["isCompleted"]);
            Assert.Equal("11.2.1787.0", info.Payload["appVersion"]);
            Assert.Equal("WinGet", info.Payload["appType"]);
            Assert.Equal("Detected", info.Payload["detectionResult"]);
        }

        [Fact]
        public void AppStateChange_Error_payload_carries_error_fields()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            var app = AppPackageState.Restore(
                id: "app-err",
                listPos: 0,
                name: "Failing App",
                runAs: AppRunAs.System,
                intent: AppIntent.Install,
                targeted: AppTargeted.Device,
                dependsOn: new HashSet<string>(),
                installationState: AppInstallationState.Error,
                downloadingOrInstallingSeen: true,
                progressPercent: 0,
                bytesDownloaded: 0,
                bytesTotal: 0,
                errorPatternId: "IME-ERROR-UNMAPPED-EXIT",
                errorDetail: "Admin did NOT set mapping for lpExitCode: 60001",
                errorCode: "60001",
                exitCode: "60001",
                hresultFromWin32: "-2146964895",
                attemptNumber: 1,
                detectionResult: "NotDetected");

            adapter.TriggerAppStateFromTest(app, AppInstallationState.Installing, AppInstallationState.Error);

            var info = Assert.Single(f.InfoEvents(SharedEventTypes.AppInstallFailed));
            Assert.Equal("true", info.Payload!["isError"]);
            Assert.Equal("IME-ERROR-UNMAPPED-EXIT", info.Payload["errorPatternId"]);
            Assert.Equal("60001", info.Payload["errorCode"]);
            Assert.Equal("-2146964895", info.Payload["hresultFromWin32"]);
            Assert.Equal(EventSeverity.Error.ToString(), info.Payload[SignalPayloadKeys.Severity]);
        }

        [Fact]
        public void Adapter_preserves_prior_Action_handlers_chain_invoke()
        {
            using var f = new Fixture();
            int priorEspCalls = 0;
            int priorUserCalls = 0;
            f.Tracker.OnEspPhaseChanged = _ => priorEspCalls++;
            f.Tracker.OnUserSessionCompleted = () => priorUserCalls++;

            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            // Call through the tracker's Action (simulating what the tracker would do).
            f.Tracker.OnEspPhaseChanged("DeviceSetup");
            f.Tracker.OnUserSessionCompleted();

            Assert.Equal(1, priorEspCalls);
            Assert.Equal(1, priorUserCalls);
            // 2 DecisionSignals + 2 InformationalEvents = 4 total.
            Assert.Equal(4, f.Ingress.Posted.Count);
        }

        [Fact]
        public void Dispose_restores_prior_Action_handlers()
        {
            using var f = new Fixture();
            int priorCalls = 0;
            Action<string> priorAction = _ => priorCalls++;
            f.Tracker.OnEspPhaseChanged = priorAction;

            var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            Assert.NotSame(priorAction, f.Tracker.OnEspPhaseChanged);

            adapter.Dispose();
            Assert.Same(priorAction, f.Tracker.OnEspPhaseChanged);
        }

        [Fact]
        public void Ctor_null_args_throw()
        {
            using var f = new Fixture();
            Assert.Throws<ArgumentNullException>(() => new ImeLogTrackerAdapter(null!, f.Ingress, f.Clock));
            Assert.Throws<ArgumentNullException>(() => new ImeLogTrackerAdapter(f.Tracker, null!, f.Clock));
            Assert.Throws<ArgumentNullException>(() => new ImeLogTrackerAdapter(f.Tracker, f.Ingress, null!));
        }

        // -- 5.9b: OnImeAgentVersion / OnDoTelemetryReceived / OnScriptCompleted --------

        [Fact]
        public void ImeAgentVersion_emits_ime_agent_version_info_event()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerImeAgentVersionFromTest("1.101.109.0");

            var info = Assert.Single(f.InfoEvents(SharedEventTypes.ImeAgentVersion));
            Assert.Equal("ImeLogTracker", info.Payload![SignalPayloadKeys.Source]);
            Assert.Equal("1.101.109.0", info.Payload["agentVersion"]);
            Assert.Equal("IME Agent version: 1.101.109.0", info.Payload[SignalPayloadKeys.Message]);
        }

        [Fact]
        public void ImeAgentVersion_null_or_empty_is_skipped()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerImeAgentVersionFromTest(null!);
            adapter.TriggerImeAgentVersionFromTest("");

            Assert.Empty(f.Ingress.Posted);
        }

        [Fact]
        public void ImeAgentVersion_same_version_twice_is_not_deduped_matches_V1_behavior()
        {
            // V1 reference session shows 2 ime_agent_version events for the same version
            // in the same session (seq 24 and 60). Adapter must not dedup.
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerImeAgentVersionFromTest("1.101.109.0");
            adapter.TriggerImeAgentVersionFromTest("1.101.109.0");

            Assert.Equal(2, f.InfoEvents(SharedEventTypes.ImeAgentVersion).Count);
        }

        [Fact]
        public void DoTelemetry_emits_do_telemetry_info_event_with_do_fields()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            var app = AppPackageState.Restore(
                id: "0e3be4ce-790b-4081-831c-d72d92f3cc9b",
                listPos: 0,
                name: "SAP 2024 Patch 13",
                runAs: AppRunAs.System,
                intent: AppIntent.Install,
                targeted: AppTargeted.Device,
                dependsOn: new HashSet<string>(),
                installationState: AppInstallationState.Downloading,
                downloadingOrInstallingSeen: true,
                progressPercent: 97,
                bytesDownloaded: 185_023_504,
                bytesTotal: 190_266_384,
                doFileSize: 190_266_384,
                doTotalBytesDownloaded: 190_266_384,
                doBytesFromPeers: 0,
                doPercentPeerCaching: 0,
                doBytesFromHttp: 190_266_384,
                doDownloadMode: 1,
                doDownloadDuration: "00:01:45.159",
                hasDoTelemetry: true);

            adapter.TriggerDoTelemetryFromTest(app);

            var info = Assert.Single(f.InfoEvents(SharedEventTypes.DoTelemetry));
            Assert.Equal("ImeLogTracker", info.Payload![SignalPayloadKeys.Source]);
            Assert.Equal(app.Id, info.Payload["appId"]);
            Assert.Equal("SAP 2024 Patch 13", info.Payload["appName"]);
            Assert.Equal("Downloading", info.Payload["state"]);
            Assert.Equal("97", info.Payload["progressPercent"]);
            Assert.Equal("185023504", info.Payload["bytesDownloaded"]);
            Assert.Equal("190266384", info.Payload["doFileSize"]);
            Assert.Equal("0", info.Payload["doBytesFromPeers"]);
            Assert.Equal("190266384", info.Payload["doBytesFromHttp"]);
            Assert.Equal("1", info.Payload["doDownloadMode"]);
            Assert.Equal("00:01:45.159", info.Payload["doDownloadDuration"]);
        }

        [Fact]
        public void DoTelemetry_null_app_or_empty_id_is_ignored()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerDoTelemetryFromTest(null!);

            Assert.Empty(f.Ingress.Posted);
        }

        [Fact]
        public void ScriptCompleted_platform_success_emits_script_completed_info_source_IME()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            var script = new ScriptExecutionState
            {
                PolicyId = "4453d1ad-8171-4459-ae6b-ad97f722ea11",
                ScriptType = "platform",
                ExitCode = 0,
                RunContext = "System",
                Result = "Success",
                Stdout = "Script created at: C:\\ProgramData\\TPF IT\\ShowItemsOnDesktop\\ShowItemsOnDesktop.ps1",
            };

            adapter.TriggerScriptCompletedFromTest(script);

            var info = Assert.Single(f.InfoEvents(SharedEventTypes.ScriptCompleted));
            // V1 contract: Source="IME" (not "ImeLogTracker") for script_completed.
            Assert.Equal("IME", info.Payload![SignalPayloadKeys.Source]);
            Assert.Equal(script.PolicyId, info.Payload["policyId"]);
            Assert.Equal("platform", info.Payload["scriptType"]);
            Assert.Equal("0", info.Payload["exitCode"]);
            Assert.Equal("System", info.Payload["runContext"]);
            Assert.Equal("Success", info.Payload["result"]);
            Assert.Equal(EventSeverity.Info.ToString(), info.Payload[SignalPayloadKeys.Severity]);
            Assert.Contains("Platform script 4453d1ad: Success (exit: 0)", info.Payload[SignalPayloadKeys.Message]);
        }

        [Fact]
        public void ScriptCompleted_platform_failure_marks_severity_Error()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            var script = new ScriptExecutionState
            {
                PolicyId = "deadbeef-0000-0000-0000-000000000000",
                ScriptType = "platform",
                ExitCode = 1,
                RunContext = "System",
                Result = "Failed",
                Stderr = "Script failed: access denied",
            };

            adapter.TriggerScriptCompletedFromTest(script);

            var info = Assert.Single(f.InfoEvents(SharedEventTypes.ScriptCompleted));
            Assert.Equal(EventSeverity.Error.ToString(), info.Payload![SignalPayloadKeys.Severity]);
            Assert.Equal("Failed", info.Payload["result"]);
            Assert.Equal("1", info.Payload["exitCode"]);
        }

        [Fact]
        public void ScriptCompleted_remediation_carries_compliance_result()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            var script = new ScriptExecutionState
            {
                PolicyId = "cafebabe-0000-0000-0000-000000000000",
                ScriptType = "remediation",
                ScriptPart = "detection",
                ExitCode = 0,
                RunContext = "System",
                ComplianceResult = "True",
            };

            adapter.TriggerScriptCompletedFromTest(script);

            var info = Assert.Single(f.InfoEvents(SharedEventTypes.ScriptCompleted));
            Assert.Equal("remediation", info.Payload!["scriptType"]);
            Assert.Equal("detection", info.Payload["scriptPart"]);
            Assert.Equal("True", info.Payload["complianceResult"]);
            Assert.Contains("Remediation script cafebabe: compliance=True", info.Payload[SignalPayloadKeys.Message]);
        }

        [Fact]
        public void ScriptCompleted_null_or_empty_policyId_is_ignored()
        {
            using var f = new Fixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerScriptCompletedFromTest(null!);
            adapter.TriggerScriptCompletedFromTest(new ScriptExecutionState { PolicyId = null });
            adapter.TriggerScriptCompletedFromTest(new ScriptExecutionState { PolicyId = "" });

            Assert.Empty(f.Ingress.Posted);
        }

        [Fact]
        public void New_callbacks_preserve_prior_action_handlers_chain_invoke()
        {
            using var f = new Fixture();
            int priorVersionCalls = 0;
            int priorDoCalls = 0;
            int priorScriptCalls = 0;
            f.Tracker.OnImeAgentVersion = _ => priorVersionCalls++;
            f.Tracker.OnDoTelemetryReceived = _ => priorDoCalls++;
            f.Tracker.OnScriptCompleted = _ => priorScriptCalls++;

            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            f.Tracker.OnImeAgentVersion("1.0.0.0");
            f.Tracker.OnDoTelemetryReceived(new AppPackageState("app-1", 0));
            f.Tracker.OnScriptCompleted(new ScriptExecutionState { PolicyId = "p", ScriptType = "platform" });

            Assert.Equal(1, priorVersionCalls);
            Assert.Equal(1, priorDoCalls);
            Assert.Equal(1, priorScriptCalls);
            Assert.Equal(3, f.AllInfoEvents().Count);
        }

        [Fact]
        public void Dispose_restores_all_prior_action_handlers()
        {
            using var f = new Fixture();
            Action<string> priorVersion = _ => { };
            Action<AppPackageState> priorDo = _ => { };
            Action<ScriptExecutionState> priorScript = _ => { };
            f.Tracker.OnImeAgentVersion = priorVersion;
            f.Tracker.OnDoTelemetryReceived = priorDo;
            f.Tracker.OnScriptCompleted = priorScript;

            var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            Assert.NotSame(priorVersion, f.Tracker.OnImeAgentVersion);
            Assert.NotSame(priorDo, f.Tracker.OnDoTelemetryReceived);
            Assert.NotSame(priorScript, f.Tracker.OnScriptCompleted);

            adapter.Dispose();
            Assert.Same(priorVersion, f.Tracker.OnImeAgentVersion);
            Assert.Same(priorDo, f.Tracker.OnDoTelemetryReceived);
            Assert.Same(priorScript, f.Tracker.OnScriptCompleted);
        }
    }
}
