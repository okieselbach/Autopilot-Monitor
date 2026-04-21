#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Termination;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.DecisionCore.State;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Termination
{
    public sealed class EnrollmentTerminationHandlerTests
    {
        private static DateTime StartUtc => new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc);
        private static DateTime EndUtc => StartUtc.AddMinutes(10);

        /// <summary>
        /// CleanupService is virtual for testability. <c>ExecuteSelfDestruct</c> is a best-effort
        /// fire-and-forget in production — in tests we just count invocations.
        /// </summary>
        private sealed class RecordingCleanupService : CleanupService
        {
            public int Invocations;
            public RecordingCleanupService(AgentConfiguration config, AgentLogger logger) : base(config, logger) { }
            public override void ExecuteSelfDestruct() => Interlocked.Increment(ref Invocations);
        }

        private sealed class Rig : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public string StateDir { get; }
            public AgentLogger Logger { get; }
            public DecisionState State { get; set; } = DecisionState.CreateInitial("S1", "T1");
            public AppPackageStateList Packages { get; }
            public RecordingCleanupService CleanupService { get; }
            public int DiagnosticsUploads { get; private set; }
            public bool? LastDiagnosticsSucceededFlag { get; private set; }
            public string? LastDiagnosticsSuffix { get; private set; }
            public DiagnosticsUploadResult? DiagnosticsResult { get; set; }
            public int ShutdownSignalled;

            public Rig()
            {
                StateDir = Path.Combine(Tmp.Path, "State");
                Logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
                Packages = new AppPackageStateList(Logger);
                CleanupService = new RecordingCleanupService(BuildConfig(), Logger);
            }

            public AgentConfiguration BuildConfig(
                bool selfDestruct = true,
                bool showDialog = false,
                bool diagEnabled = false,
                string diagMode = "Off")
            {
                return new AgentConfiguration
                {
                    SessionId = "S1",
                    TenantId = "T1",
                    ApiBaseUrl = "http://localhost",
                    SelfDestructOnComplete = selfDestruct,
                    ShowEnrollmentSummary = showDialog,
                    DiagnosticsUploadEnabled = diagEnabled,
                    DiagnosticsUploadMode = diagMode,
                    EnrollmentSummaryTimeoutSeconds = 60,
                };
            }

            public EnrollmentTerminationHandler Build(AgentConfiguration? config = null)
            {
                config ??= BuildConfig();
                return new EnrollmentTerminationHandler(
                    configuration: config,
                    logger: Logger,
                    stateDirectory: StateDir,
                    agentStartTimeUtc: StartUtc,
                    currentStateAccessor: () => State,
                    packageStatesAccessor: () => Packages,
                    cleanupServiceFactory: () => CleanupService,
                    uploadDiagnosticsAsync: (succeeded, suffix) =>
                    {
                        DiagnosticsUploads++;
                        LastDiagnosticsSucceededFlag = succeeded;
                        LastDiagnosticsSuffix = suffix;
                        return Task.FromResult(DiagnosticsResult ?? new DiagnosticsUploadResult { BlobName = "blob" });
                    },
                    signalShutdown: () => Interlocked.Increment(ref ShutdownSignalled));
            }

            public void Dispose() => Tmp.Dispose();
        }

        private static EnrollmentTerminatedEventArgs Args(
            EnrollmentTerminationReason reason,
            EnrollmentTerminationOutcome outcome,
            SessionStage stage,
            DateTime? at = null) =>
            new EnrollmentTerminatedEventArgs(
                reason, outcome, stage.ToString(), at ?? EndUtc);

        [Fact]
        public void Handle_writes_final_status_json_in_state_directory()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.Completed }.Build();

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.Completed));

            var statusPath = Path.Combine(rig.StateDir, SummaryDialogLauncher.FinalStatusFileName);
            Assert.True(File.Exists(statusPath), "final-status.json should be written even when ShowEnrollmentSummary=false");
        }

        [Fact]
        public void Handle_writes_enrollment_complete_marker_on_non_whiteglove_terminal()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.Completed }.Build();

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.Completed));

            Assert.True(File.Exists(Path.Combine(rig.StateDir, "enrollment-complete.marker")));
        }

        [Fact]
        public void Handle_skips_marker_and_cleanup_on_whiteglove_part1_exit()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.WhiteGloveSealed }.Build();

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.WhiteGloveSealed));

            Assert.False(File.Exists(Path.Combine(rig.StateDir, "enrollment-complete.marker")));
            Assert.Equal(0, rig.CleanupService.Invocations);
        }

        [Fact]
        public void Handle_runs_cleanup_on_success_when_self_destruct_enabled()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.Completed }.Build();

            rig.Build().Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.Completed));

            Assert.Equal(1, rig.CleanupService.Invocations);
        }

        [Fact]
        public void Handle_skips_cleanup_when_self_destruct_disabled()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.Completed }.Build();
            var cfg = rig.BuildConfig(selfDestruct: false);

            rig.Build(cfg).Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Succeeded, SessionStage.Completed));

            Assert.Equal(0, rig.CleanupService.Invocations);
        }

        [Fact]
        public void Handle_signals_shutdown_exactly_once_even_on_error()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.Failed }.Build();

            var sut = rig.Build();
            sut.Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Failed, SessionStage.Failed));

            // Idempotent — second Handle is a no-op.
            sut.Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Failed, SessionStage.Failed));

            Assert.Equal(1, rig.ShutdownSignalled);
        }

        [Theory]
        [InlineData("Off", EnrollmentTerminationOutcome.Succeeded, false, 0)]
        [InlineData("Always", EnrollmentTerminationOutcome.Succeeded, true, 1)]
        [InlineData("Always", EnrollmentTerminationOutcome.Failed, true, 1)]
        [InlineData("OnFailure", EnrollmentTerminationOutcome.Succeeded, true, 0)]
        [InlineData("OnFailure", EnrollmentTerminationOutcome.Failed, true, 1)]
        public void Handle_diagnostics_upload_respects_mode_and_outcome(
            string mode, EnrollmentTerminationOutcome outcome, bool enabled, int expected)
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.Completed }.Build();
            var cfg = rig.BuildConfig(diagEnabled: enabled, diagMode: mode, selfDestruct: false);

            rig.Build(cfg).Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, outcome,
                    outcome == EnrollmentTerminationOutcome.Succeeded ? SessionStage.Completed : SessionStage.Failed));

            Assert.Equal(expected, rig.DiagnosticsUploads);
        }

        [Fact]
        public void Handle_diagnostics_upload_suffix_reflects_success_vs_failure()
        {
            using var rig = new Rig();
            rig.State = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1")) { Stage = SessionStage.Failed }.Build();
            var cfg = rig.BuildConfig(diagEnabled: true, diagMode: "Always", selfDestruct: false);

            rig.Build(cfg).Handle(sender: null!,
                Args(EnrollmentTerminationReason.DecisionTerminalStage, EnrollmentTerminationOutcome.Failed, SessionStage.Failed));

            Assert.Equal("failure", rig.LastDiagnosticsSuffix);
            Assert.Equal(false, rig.LastDiagnosticsSucceededFlag);
        }
    }
}
