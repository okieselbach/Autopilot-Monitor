using System;
using System.IO;
using AutopilotMonitor.Agent.Core.Configuration;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Agent.Core.Monitoring.Runtime;
using Xunit;

namespace AutopilotMonitor.Agent.Core.Tests.Core
{
    /// <summary>
    /// Verifies that MonitoringService rejects an incomplete AgentConfiguration with a
    /// diagnostic message that names the missing fields. Customer-impact regression
    /// (gardnermedia, 2026-04-30): a TenantId-less startup crashed with an opaque
    /// "Invalid agent configuration" message that defied forensic analysis.
    /// </summary>
    public class MonitoringServiceConfigValidationTests : IDisposable
    {
        private readonly string _tempDir;

        public MonitoringServiceConfigValidationTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "autopilot-cfg-validation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }

        private AgentConfiguration BuildValidConfig() => new AgentConfiguration
        {
            ApiBaseUrl = "https://example.invalid",
            SessionId = Guid.NewGuid().ToString(),
            TenantId = Guid.NewGuid().ToString(),
            SpoolDirectory = Path.Combine(_tempDir, "spool"),
            LogDirectory = Path.Combine(_tempDir, "logs")
        };

        private AgentLogger BuildLogger() => new AgentLogger(Path.Combine(_tempDir, "logs"));

        [Fact]
        public void Constructor_throws_with_field_name_when_TenantId_missing()
        {
            var config = BuildValidConfig();
            config.TenantId = null;

            var ex = Assert.Throws<InvalidOperationException>(
                () => new MonitoringService(config, BuildLogger(), agentVersion: "test"));

            Assert.Contains("missing", ex.Message);
            Assert.Contains(nameof(AgentConfiguration.TenantId), ex.Message);
        }

        [Fact]
        public void Constructor_throws_with_field_name_when_SessionId_missing()
        {
            var config = BuildValidConfig();
            config.SessionId = "";

            var ex = Assert.Throws<InvalidOperationException>(
                () => new MonitoringService(config, BuildLogger(), agentVersion: "test"));

            Assert.Contains(nameof(AgentConfiguration.SessionId), ex.Message);
        }

        [Fact]
        public void Constructor_throws_with_field_name_when_ApiBaseUrl_missing()
        {
            var config = BuildValidConfig();
            config.ApiBaseUrl = null;

            var ex = Assert.Throws<InvalidOperationException>(
                () => new MonitoringService(config, BuildLogger(), agentVersion: "test"));

            Assert.Contains(nameof(AgentConfiguration.ApiBaseUrl), ex.Message);
        }

        [Fact]
        public void Constructor_throws_with_all_field_names_when_multiple_missing()
        {
            var config = new AgentConfiguration
            {
                ApiBaseUrl = null,
                SessionId = null,
                TenantId = null,
                SpoolDirectory = Path.Combine(_tempDir, "spool"),
                LogDirectory = Path.Combine(_tempDir, "logs")
            };

            var ex = Assert.Throws<InvalidOperationException>(
                () => new MonitoringService(config, BuildLogger(), agentVersion: "test"));

            Assert.Contains(nameof(AgentConfiguration.ApiBaseUrl), ex.Message);
            Assert.Contains(nameof(AgentConfiguration.SessionId), ex.Message);
            Assert.Contains(nameof(AgentConfiguration.TenantId), ex.Message);
        }

        [Fact]
        public void Constructor_message_does_not_change_for_valid_config()
        {
            // Sanity: a fully-populated config should NOT throw the validation exception.
            // It may throw later (cert load, spool init) — we only assert the validation
            // path itself does not fire on a complete config.
            var config = BuildValidConfig();

            var ex = Record.Exception(() => new MonitoringService(config, BuildLogger(), agentVersion: "test"));
            if (ex is InvalidOperationException ioe)
                Assert.DoesNotContain("Invalid agent configuration:", ioe.Message);
        }
    }
}
