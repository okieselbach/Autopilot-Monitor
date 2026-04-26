using System;
using System.IO;
using System.Net.Http;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Runtime;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Program
{
    /// <summary>
    /// Tests for <see cref="BackendSessionRegistration"/>: Phase 6 extract from
    /// <c>Program.RunAgent</c>. The Register path delegates to
    /// <see cref="SessionRegistrationHelper"/> (covered by SessionRegistrationHelperTests);
    /// here we cover the wrapper's Result-type contract and argument validation.
    /// </summary>
    public sealed class BackendSessionRegistrationTests
    {
        private static AgentLogger NewLogger(string path)
            => new AgentLogger(Path.Combine(path, "logs"), AgentLogLevel.Info);

        [Fact]
        public void Outcome_Exit_with_six_signals_auth_failed()
        {
            var result = SessionRegistrationOutcomeResult.Exit(6);

            Assert.True(result.ShouldExit);
            Assert.Equal(6, result.ExitCode);
            Assert.Null(result.Registration);
        }

        [Fact]
        public void Outcome_Exit_with_seven_signals_non_auth_failed()
        {
            var result = SessionRegistrationOutcomeResult.Exit(7);

            Assert.True(result.ShouldExit);
            Assert.Equal(7, result.ExitCode);
            Assert.Null(result.Registration);
        }

        [Fact]
        public void Outcome_Continue_carries_successful_registration_and_zero_exit_code()
        {
            var registration = SessionRegistrationResult.Succeeded(new RegisterSessionResponse
            {
                Success = true,
                AdminAction = null,
            });

            var result = SessionRegistrationOutcomeResult.Continue(registration);

            Assert.False(result.ShouldExit);
            Assert.Equal(0, result.ExitCode);
            Assert.Same(registration, result.Registration);
        }

        [Fact]
        public void Register_throws_on_null_arguments()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var config = new AgentConfiguration { ApiBaseUrl = "https://example.invalid", TenantId = "t", SessionId = "s" };
            var auth = BackendClientFactory.BuildAuthClients(config, agentVersion: "1.0", logger: logger);

            using var http = new HttpClient();

            Assert.Throws<ArgumentNullException>(
                () => BackendSessionRegistration.Register(null, auth, http, "1.0", consoleMode: false, logger: logger));

            Assert.Throws<ArgumentNullException>(
                () => BackendSessionRegistration.Register(config, auth: null, http, "1.0", consoleMode: false, logger: logger));

            Assert.Throws<ArgumentNullException>(
                () => BackendSessionRegistration.Register(config, auth, http, "1.0", consoleMode: false, logger: null));
        }
    }
}
