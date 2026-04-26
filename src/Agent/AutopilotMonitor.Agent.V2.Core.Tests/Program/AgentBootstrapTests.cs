using System;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Runtime;
using Newtonsoft.Json;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Program
{
    /// <summary>
    /// Tests for <see cref="AgentBootstrap.Run"/>: the Phase 1+2 extract from
    /// <c>Program.RunAgent</c>. Covers the V1-parity exit-code mapping
    /// (0 = guard handled, 2 = no TenantId, 3 = await timeout) and the Continue-payload shape.
    /// </summary>
    public sealed class AgentBootstrapTests
    {
        private const string EnrollmentCompleteMarkerFileName = "enrollment-complete.marker";
        private const string StateSubdirectory = "State";

        private static AgentLogger NewLogger(string path)
            => new AgentLogger(Path.Combine(path, "logs"), AgentLogLevel.Info);

        private static string EnsureStateDir(string root)
        {
            var stateDir = Path.Combine(root, StateSubdirectory);
            Directory.CreateDirectory(stateDir);
            return stateDir;
        }

        [Fact]
        public void Run_returns_exit_zero_when_enrollment_complete_marker_present()
        {
            using var tmp = new TempDirectory();
            var stateDir = EnsureStateDir(tmp.Path);
            File.WriteAllText(Path.Combine(stateDir, EnrollmentCompleteMarkerFileName), "previous session completed");

            var logger = NewLogger(tmp.Path);
            // --no-cleanup disables SelfDestructOnComplete so the marker-handler short-circuits
            // without invoking the real CleanupService (no Scheduled-Task / file side effects).
            var args = new[] { "--no-cleanup" };

            var result = AgentBootstrap.Run(args, logger, tmp.Path, Path.Combine(tmp.Path, "logs"), stateDir, consoleMode: false);

            Assert.True(result.ShouldExit);
            Assert.Equal(0, result.ExitCode);
        }

        [Fact]
        public void Run_returns_exit_zero_when_session_age_exceeds_emergency_break_max()
        {
            using var tmp = new TempDirectory();
            var stateDir = EnsureStateDir(tmp.Path);

            // Seed a session whose creation timestamp is far older than the default
            // AbsoluteMaxSessionHours (24h in BuildAgentConfiguration's default path).
            var persistence = new SessionIdPersistence(tmp.Path);
            persistence.GetOrCreate();
            persistence.SaveSessionCreatedAt(DateTime.UtcNow.AddDays(-30));

            var logger = NewLogger(tmp.Path);
            var args = new[] { "--no-cleanup" };

            var result = AgentBootstrap.Run(args, logger, tmp.Path, Path.Combine(tmp.Path, "logs"), stateDir, consoleMode: false);

            Assert.True(result.ShouldExit);
            Assert.Equal(0, result.ExitCode);
        }

        [Fact]
        public void Run_returns_continue_when_bootstrap_config_provides_tenant()
        {
            using var tmp = new TempDirectory();
            var stateDir = EnsureStateDir(tmp.Path);

            // Persist a bootstrap-config.json so the TenantId resolution falls back to it
            // when the registry has no enrollment for this test machine.
            var bootstrapConfig = new
            {
                tenantId = "00000000-0000-0000-0000-000000000123",
                bootstrapToken = "dummy-token-for-test"
            };
            File.WriteAllText(
                Path.Combine(tmp.Path, "bootstrap-config.json"),
                JsonConvert.SerializeObject(bootstrapConfig));

            var logger = NewLogger(tmp.Path);
            var args = new[] { "--no-cleanup", "--bootstrap-token", "dummy-token-for-test" };

            var result = AgentBootstrap.Run(args, logger, tmp.Path, Path.Combine(tmp.Path, "logs"), stateDir, consoleMode: false);

            Assert.False(result.ShouldExit);
            Assert.NotNull(result.AgentConfig);
            Assert.NotNull(result.SessionPersistence);
            Assert.NotNull(result.PreviousExit);
            Assert.NotNull(result.CleanupServiceFactory);
            // TenantId may come from registry on a real enrollment-host, or from bootstrap-config
            // on a fresh test box. Either is valid; we only require it to be non-empty.
            Assert.False(string.IsNullOrEmpty(result.AgentConfig.TenantId));
            // SessionId is provisioned by GetOrCreate inside Bootstrap.
            Assert.False(string.IsNullOrEmpty(result.AgentConfig.SessionId));
        }

        [Fact]
        public void Run_handles_first_run_previousExit_classification()
        {
            using var tmp = new TempDirectory();
            var stateDir = EnsureStateDir(tmp.Path);

            // No prior markers, no clean-exit, no crash log → DetectPreviousExit returns first_run.
            var bootstrapConfig = new { tenantId = "00000000-0000-0000-0000-000000000123" };
            File.WriteAllText(
                Path.Combine(tmp.Path, "bootstrap-config.json"),
                JsonConvert.SerializeObject(bootstrapConfig));

            var logger = NewLogger(tmp.Path);
            var args = new[] { "--no-cleanup", "--bootstrap-token", "dummy" };

            var result = AgentBootstrap.Run(args, logger, tmp.Path, Path.Combine(tmp.Path, "logs"), stateDir, consoleMode: false);

            Assert.False(result.ShouldExit);
            Assert.Equal("first_run", result.PreviousExit.ExitType);
            Assert.False(result.IsWhiteGloveResume);
        }
    }
}
