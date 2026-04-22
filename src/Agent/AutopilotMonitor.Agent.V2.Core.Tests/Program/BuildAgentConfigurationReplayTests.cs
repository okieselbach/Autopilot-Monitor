using System;
using AutopilotMonitor.Agent.V2;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Program
{
    /// <summary>
    /// Verifies that <see cref="AutopilotMonitor.Agent.V2.Program.BuildAgentConfiguration"/>
    /// propagates the dev-only <c>--replay-log-dir</c> + <c>--replay-speed-factor</c> CLI flags
    /// onto <see cref="AgentConfiguration"/> so the IME log replay SimulationMode actually
    /// activates downstream. V2 parity PR-A.
    /// </summary>
    public sealed class BuildAgentConfigurationReplayTests
    {
        private static AgentConfiguration Build(params string[] args) =>
            AutopilotMonitor.Agent.V2.Program.BuildAgentConfiguration(
                args: args,
                tenantId: "tenant-t",
                sessionId: "session-s",
                bootstrapConfig: null,
                awaitConfig: null);

        [Fact]
        public void Without_replay_flags_ReplayLogDir_stays_null_and_speed_factor_default()
        {
            var cfg = Build();

            Assert.Null(cfg.ReplayLogDir);
            Assert.Equal(50.0, cfg.ReplaySpeedFactor);
        }

        [Fact]
        public void Replay_log_dir_populates_configuration()
        {
            var cfg = Build("--replay-log-dir", @"C:\tmp\ime-logs");

            Assert.Equal(@"C:\tmp\ime-logs", cfg.ReplayLogDir);
        }

        [Fact]
        public void Replay_speed_factor_is_parsed_as_invariant_double()
        {
            // Comma locale must NOT flip the meaning — parser uses InvariantCulture.
            var cfg = Build("--replay-log-dir", @"C:\tmp\x", "--replay-speed-factor", "125.5");

            Assert.Equal(125.5, cfg.ReplaySpeedFactor);
        }

        [Fact]
        public void Replay_speed_factor_zero_is_rejected_and_default_kept()
        {
            var cfg = Build("--replay-log-dir", @"C:\tmp\x", "--replay-speed-factor", "0");

            Assert.Equal(50.0, cfg.ReplaySpeedFactor);
        }

        [Fact]
        public void Replay_speed_factor_malformed_is_rejected_and_default_kept()
        {
            var cfg = Build("--replay-log-dir", @"C:\tmp\x", "--replay-speed-factor", "fast");

            Assert.Equal(50.0, cfg.ReplaySpeedFactor);
        }
    }
}
