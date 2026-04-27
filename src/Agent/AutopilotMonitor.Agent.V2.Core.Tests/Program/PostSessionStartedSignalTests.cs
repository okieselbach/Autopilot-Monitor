using System.IO;
using System.Reflection;
using AutopilotMonitor.Agent.V2;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Program
{
    /// <summary>
    /// Verifies that <see cref="AutopilotMonitor.Agent.V2.Runtime.LifecycleEmitters.PostSessionStarted"/>
    /// emits the SessionStarted decision signal with the registration metadata payload the
    /// reducer anchor needs. Plan §M4-V2-parity PR-C.
    /// </summary>
    public sealed class PostSessionStartedSignalTests
    {
        private static AgentLogger NewLogger(string path)
            => new AgentLogger(Path.Combine(path, "logs"), AgentLogLevel.Info);

        private static SessionRegistrationResult BuildSucceeded(ValidatorType validatedBy, string? adminAction = null)
        {
            var response = new RegisterSessionResponse
            {
                SessionId = "session-anon-1",
                Success = true,
                Message = "ok",
                ValidatedBy = validatedBy,
                AdminAction = adminAction,
            };

            // SessionRegistrationResult uses a private ctor + public static factories. Call
            // Succeeded via reflection-free factory — it's public.
            return SessionRegistrationResult.Succeeded(response);
        }

        private static AgentConfiguration BuildConfig(bool bootstrap)
        {
            return new AgentConfiguration
            {
                ApiBaseUrl = "https://example.invalid",
                SessionId = "session-anon-1",
                TenantId = "tenant-anon-1",
                SpoolDirectory = Path.GetTempPath(),
                LogDirectory = Path.GetTempPath(),
                LogLevel = AgentLogLevel.Info,
                UseClientCertAuth = !bootstrap,
                UseBootstrapTokenAuth = bootstrap,
                BootstrapToken = bootstrap ? "dummy" : null,
            };
        }

        [Fact]
        public void Posts_session_started_kind_with_default_schema_version()
        {
            using var tmp = new TempDirectory();
            var sink = new FakeSignalIngressSink();
            var logger = NewLogger(tmp.Path);
            var result = BuildSucceeded(ValidatorType.AutopilotV1);

            AutopilotMonitor.Agent.V2.Runtime.LifecycleEmitters.PostSessionStarted(sink, result, BuildConfig(bootstrap: false), agentVersion: "1.2.3.4", logger);

            Assert.Single(sink.Posted);
            var posted = sink.Posted[0];
            Assert.Equal(DecisionSignalKind.SessionStarted, posted.Kind);
            Assert.Equal(1, posted.KindSchemaVersion);
            Assert.Equal("Program.RunAgent", posted.SourceOrigin);
            Assert.Equal(EvidenceKind.Synthetic, posted.Evidence.Kind);
            Assert.Equal("register_session_success", posted.Evidence.Identifier);
        }

        [Fact]
        public void Payload_carries_only_lifecycle_anchor_metadata()
        {
            // V2 race-fix (10c8e0bf debrief, 2026-04-26) — enrollmentType / isHybridJoin
            // moved to DecisionSignalKind.EnrollmentFactsObserved (covered by
            // PostEnrollmentFactsObservedSignalTests). SessionStarted is now a pure
            // lifecycle anchor with only the registration-metadata payload.
            using var tmp = new TempDirectory();
            var sink = new FakeSignalIngressSink();
            var logger = NewLogger(tmp.Path);
            var result = BuildSucceeded(ValidatorType.CorporateIdentifier);

            AutopilotMonitor.Agent.V2.Runtime.LifecycleEmitters.PostSessionStarted(sink, result, BuildConfig(bootstrap: true), agentVersion: "1.2.3.4", logger);

            var payload = sink.Posted[0].Payload;
            Assert.NotNull(payload);
            Assert.Equal("CorporateIdentifier", payload!["validatedBy"]);
            Assert.Equal("true", payload["isBootstrapSession"]);
            Assert.Equal("1.2.3.4", payload["agentVersion"]);
            Assert.DoesNotContain("enrollmentType", (System.Collections.Generic.IDictionary<string, string>)payload);
            Assert.DoesNotContain("isHybridJoin", (System.Collections.Generic.IDictionary<string, string>)payload);
        }

        [Fact]
        public void Swallows_sink_exceptions()
        {
            // The helper is called on the hot startup path — a misbehaving sink must not
            // crash the agent.
            using var tmp = new TempDirectory();
            var sink = new ThrowingSink();
            var logger = NewLogger(tmp.Path);
            var result = BuildSucceeded(ValidatorType.Bootstrap);

            var ex = Record.Exception(() =>
                AutopilotMonitor.Agent.V2.Runtime.LifecycleEmitters.PostSessionStarted(sink, result, BuildConfig(bootstrap: false), agentVersion: "1.2.3.4", logger));

            Assert.Null(ex);
        }

        private sealed class ThrowingSink : AutopilotMonitor.Agent.V2.Core.Orchestration.ISignalIngressSink
        {
            public void Post(
                DecisionSignalKind kind,
                System.DateTime occurredAtUtc,
                string sourceOrigin,
                Evidence evidence,
                System.Collections.Generic.IReadOnlyDictionary<string, string>? payload = null,
                int kindSchemaVersion = 1,
                object? typedPayload = null)
            {
                throw new System.InvalidOperationException("ingress unavailable");
            }
        }
    }
}
