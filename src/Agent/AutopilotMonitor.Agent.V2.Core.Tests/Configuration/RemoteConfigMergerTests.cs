using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Configuration
{
    /// <summary>
    /// Tests for <see cref="RemoteConfigMerger"/>. This merger is the single source of
    /// Legacy-parity: every field delivered by the backend that has a live V2 consumer must
    /// land on <see cref="AgentConfiguration"/> after <c>FetchConfigAsync</c>.
    /// </summary>
    public sealed class RemoteConfigMergerTests
    {
        private static AgentConfiguration NewAgentConfig() => new AgentConfiguration
        {
            ApiBaseUrl = "https://example",
            TenantId = "t",
            SessionId = "s",
        };

        private static AgentConfigResponse FullRemote() => new AgentConfigResponse
        {
            ConfigVersion = 26,
            UploadIntervalSeconds = 45,
            MaxBatchSize = 250,
            SelfDestructOnComplete = false,
            KeepLogFile = true,
            EnableGeoLocation = false,
            RebootOnComplete = true,
            RebootDelaySeconds = 30,
            ShowEnrollmentSummary = true,
            EnrollmentSummaryTimeoutSeconds = 90,
            EnrollmentSummaryBrandingImageUrl = "https://cdn.example/logo.png",
            EnrollmentSummaryLaunchRetrySeconds = 300,
            NtpServer = "pool.ntp.org",
            EnableTimezoneAutoSet = true,
            DiagnosticsUploadEnabled = true,
            DiagnosticsUploadMode = "OnFailure",
            DiagnosticsLogPaths = new List<DiagnosticsLogPath>
            {
                new DiagnosticsLogPath { Path = @"%ProgramData%\Foo\*.log", IsBuiltIn = false },
            },
            LogLevel = "Debug",
            SendTraceEvents = false,
            UnrestrictedMode = true,
            Collectors = new CollectorConfiguration
            {
                AgentMaxLifetimeMinutes = 120,
                HelloWaitTimeoutSeconds = 45,
            },
        };

        // ============================================================= Happy path

        [Fact]
        public void Merge_applies_all_tenant_fields_when_no_cli_overrides()
        {
            var agent = NewAgentConfig();
            var remote = FullRemote();

            RemoteConfigMerger.Merge(agent, remote, Array.Empty<string>());

            Assert.False(agent.SelfDestructOnComplete);
            Assert.True(agent.KeepLogFile);
            Assert.False(agent.EnableGeoLocation);
            Assert.True(agent.RebootOnComplete);
            Assert.Equal(30, agent.RebootDelaySeconds);
            Assert.True(agent.ShowEnrollmentSummary);
            Assert.Equal(90, agent.EnrollmentSummaryTimeoutSeconds);
            Assert.Equal("https://cdn.example/logo.png", agent.EnrollmentSummaryBrandingImageUrl);
            Assert.Equal(300, agent.EnrollmentSummaryLaunchRetrySeconds);
            Assert.Equal("pool.ntp.org", agent.NtpServer);
            Assert.True(agent.EnableTimezoneAutoSet);
            Assert.True(agent.DiagnosticsUploadEnabled);
            Assert.Equal("OnFailure", agent.DiagnosticsUploadMode);
            Assert.Single(agent.DiagnosticsLogPaths);
            Assert.Equal(@"%ProgramData%\Foo\*.log", agent.DiagnosticsLogPaths[0].Path);
            Assert.Equal(AgentLogLevel.Debug, agent.LogLevel);
            Assert.False(agent.SendTraceEvents);
            Assert.True(agent.UnrestrictedMode);
            Assert.Equal(45, agent.UploadIntervalSeconds);
            Assert.Equal(250, agent.MaxBatchSize);
            Assert.Equal(120, agent.AgentMaxLifetimeMinutes);
            Assert.Equal(45, agent.HelloWaitTimeoutSeconds);
        }

        // ============================================================= CLI override wins

        [Fact]
        public void Merge_preserves_no_cleanup_cli_flag_over_remote_true()
        {
            var agent = NewAgentConfig();
            agent.SelfDestructOnComplete = false; // as set by BuildAgentConfiguration when CLI had --no-cleanup

            var remote = FullRemote();
            remote.SelfDestructOnComplete = true; // tenant enabled cleanup, dev disabled locally

            RemoteConfigMerger.Merge(agent, remote, new[] { "--no-cleanup" });

            Assert.False(agent.SelfDestructOnComplete);
        }

        [Fact]
        public void Merge_preserves_keep_logfile_cli_flag_over_remote_false()
        {
            var agent = NewAgentConfig();
            agent.KeepLogFile = true;

            var remote = FullRemote();
            remote.KeepLogFile = false;

            RemoteConfigMerger.Merge(agent, remote, new[] { "--keep-logfile" });

            Assert.True(agent.KeepLogFile);
        }

        [Fact]
        public void Merge_preserves_reboot_cli_flag_over_remote_false()
        {
            var agent = NewAgentConfig();
            agent.RebootOnComplete = true;

            var remote = FullRemote();
            remote.RebootOnComplete = false;

            RemoteConfigMerger.Merge(agent, remote, new[] { "--reboot-on-complete" });

            Assert.True(agent.RebootOnComplete);
        }

        [Fact]
        public void Merge_preserves_disable_geolocation_cli_flag_over_remote_true()
        {
            var agent = NewAgentConfig();
            agent.EnableGeoLocation = false;

            var remote = FullRemote();
            remote.EnableGeoLocation = true;

            RemoteConfigMerger.Merge(agent, remote, new[] { "--disable-geolocation" });

            Assert.False(agent.EnableGeoLocation);
        }

        [Fact]
        public void Merge_preserves_log_level_cli_arg_over_remote_string()
        {
            var agent = NewAgentConfig();
            agent.LogLevel = AgentLogLevel.Verbose;

            var remote = FullRemote();
            remote.LogLevel = "Debug";

            RemoteConfigMerger.Merge(agent, remote, new[] { "--log-level", "Verbose" });

            Assert.Equal(AgentLogLevel.Verbose, agent.LogLevel);
        }

        [Fact]
        public void Merge_parses_remote_log_level_case_insensitive()
        {
            var agent = NewAgentConfig();
            var remote = FullRemote();
            remote.LogLevel = "trace";

            RemoteConfigMerger.Merge(agent, remote, Array.Empty<string>());

            Assert.Equal(AgentLogLevel.Trace, agent.LogLevel);
        }

        [Fact]
        public void Merge_keeps_log_level_when_remote_value_is_unparseable()
        {
            var agent = NewAgentConfig();
            agent.LogLevel = AgentLogLevel.Info;

            var remote = FullRemote();
            remote.LogLevel = "NotAValidLevel";

            RemoteConfigMerger.Merge(agent, remote, Array.Empty<string>());

            Assert.Equal(AgentLogLevel.Info, agent.LogLevel);
        }

        // ============================================================= Null / partial tolerance

        [Fact]
        public void Merge_ignores_null_remote_config()
        {
            var agent = NewAgentConfig();
            agent.NtpServer = "local.ntp";
            agent.SelfDestructOnComplete = true;

            RemoteConfigMerger.Merge(agent, null!, Array.Empty<string>());

            Assert.Equal("local.ntp", agent.NtpServer);
            Assert.True(agent.SelfDestructOnComplete);
        }

        [Fact]
        public void Merge_accepts_null_cli_args()
        {
            var agent = NewAgentConfig();
            var remote = FullRemote();

            RemoteConfigMerger.Merge(agent, remote, null!);

            Assert.False(agent.SelfDestructOnComplete);
            Assert.Equal("pool.ntp.org", agent.NtpServer);
        }

        [Fact]
        public void Merge_tolerates_null_nested_collectors()
        {
            var agent = NewAgentConfig();
            agent.AgentMaxLifetimeMinutes = 360;
            agent.HelloWaitTimeoutSeconds = 30;

            var remote = FullRemote();
            remote.Collectors = null!;

            RemoteConfigMerger.Merge(agent, remote, Array.Empty<string>());

            Assert.Equal(360, agent.AgentMaxLifetimeMinutes);
            Assert.Equal(30, agent.HelloWaitTimeoutSeconds);
        }

        [Fact]
        public void Merge_keeps_default_ntp_server_when_remote_is_empty()
        {
            var agent = NewAgentConfig();
            agent.NtpServer = "time.windows.com";

            var remote = FullRemote();
            remote.NtpServer = "";

            RemoteConfigMerger.Merge(agent, remote, Array.Empty<string>());

            Assert.Equal("time.windows.com", agent.NtpServer);
        }

        [Fact]
        public void Merge_keeps_default_diagnostics_mode_when_remote_is_null()
        {
            var agent = NewAgentConfig();
            agent.DiagnosticsUploadMode = "Off";

            var remote = FullRemote();
            remote.DiagnosticsUploadMode = null!;

            RemoteConfigMerger.Merge(agent, remote, Array.Empty<string>());

            Assert.Equal("Off", agent.DiagnosticsUploadMode);
        }

        [Fact]
        public void Merge_replaces_diagnostics_log_paths_with_empty_list_when_remote_null()
        {
            var agent = NewAgentConfig();
            agent.DiagnosticsLogPaths = new List<DiagnosticsLogPath>
            {
                new DiagnosticsLogPath { Path = "stale", IsBuiltIn = false },
            };

            var remote = FullRemote();
            remote.DiagnosticsLogPaths = null!;

            RemoteConfigMerger.Merge(agent, remote, Array.Empty<string>());

            Assert.NotNull(agent.DiagnosticsLogPaths);
            Assert.Empty(agent.DiagnosticsLogPaths);
        }

        // ============================================================= Int clamping

        [Fact]
        public void Merge_skips_non_positive_upload_interval()
        {
            var agent = NewAgentConfig();
            agent.UploadIntervalSeconds = 30;

            var remote = FullRemote();
            remote.UploadIntervalSeconds = 0;

            RemoteConfigMerger.Merge(agent, remote, Array.Empty<string>());

            Assert.Equal(30, agent.UploadIntervalSeconds);
        }

        [Fact]
        public void Merge_skips_non_positive_max_batch_size()
        {
            var agent = NewAgentConfig();
            agent.MaxBatchSize = 100;

            var remote = FullRemote();
            remote.MaxBatchSize = -1;

            RemoteConfigMerger.Merge(agent, remote, Array.Empty<string>());

            Assert.Equal(100, agent.MaxBatchSize);
        }

        [Fact]
        public void Merge_skips_non_positive_hello_wait_timeout()
        {
            var agent = NewAgentConfig();
            agent.HelloWaitTimeoutSeconds = 30;

            var remote = FullRemote();
            remote.Collectors = new CollectorConfiguration
            {
                AgentMaxLifetimeMinutes = 360,
                HelloWaitTimeoutSeconds = 0,
            };

            RemoteConfigMerger.Merge(agent, remote, Array.Empty<string>());

            Assert.Equal(30, agent.HelloWaitTimeoutSeconds);
        }

        [Fact]
        public void Merge_allows_zero_agent_max_lifetime_to_disable_watchdog()
        {
            var agent = NewAgentConfig();
            agent.AgentMaxLifetimeMinutes = 360;

            var remote = FullRemote();
            remote.Collectors = new CollectorConfiguration
            {
                AgentMaxLifetimeMinutes = 0, // explicit "disabled"
                HelloWaitTimeoutSeconds = 30,
            };

            RemoteConfigMerger.Merge(agent, remote, Array.Empty<string>());

            Assert.Equal(0, agent.AgentMaxLifetimeMinutes);
        }

        // ============================================================= Guard

        [Fact]
        public void Merge_throws_on_null_agent_config()
        {
            Assert.Throws<ArgumentNullException>(() =>
                RemoteConfigMerger.Merge(null!, FullRemote(), Array.Empty<string>()));
        }

        // ============================================================= The user's scenario

        [Fact]
        public void Merge_flips_self_destruct_off_when_tenant_admin_disables_cleanup_and_agent_runs_without_cli_args()
        {
            // Regression guard for the VM smoke-test bug: tenant config has SelfDestructOnComplete=false
            // but agent was built with CLI-default true, and without the merger the remote value never
            // reached CleanupService / EnrollmentTerminationHandler.
            var agent = NewAgentConfig();
            agent.SelfDestructOnComplete = true; // BuildAgentConfiguration CLI-default when no --no-cleanup

            var remote = FullRemote();
            remote.SelfDestructOnComplete = false;

            RemoteConfigMerger.Merge(agent, remote, Array.Empty<string>());

            Assert.False(agent.SelfDestructOnComplete);
        }
    }
}
