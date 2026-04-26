using System;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.Agent.V2.Runtime;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Program
{
    /// <summary>
    /// Tests for <see cref="LifecycleEmitters"/>: PR6 extract from <c>Program.RunAgent</c>.
    /// Focused on the watchdog factory methods + the unrestricted-mode no-op gate.
    /// The decision-signal posters (PostSessionStarted etc.) are already covered by
    /// the migrated <c>PostSessionStartedSignalTests</c>.
    /// </summary>
    public sealed class LifecycleEmittersTests
    {
        private static AgentLogger NewLogger(string path)
            => new AgentLogger(Path.Combine(path, "logs"), AgentLogLevel.Info);

        private static AgentConfiguration BuildConfig() => new AgentConfiguration
        {
            ApiBaseUrl = "https://example.invalid",
            SessionId = "S1",
            TenantId = "T1",
            AgentMaxLifetimeMinutes = 360,
            MaxAuthFailures = 3,
            AuthFailureTimeoutMinutes = 30,
        };

        // ============================================================ MaxLifetime watchdog

        [Fact]
        public void MaxLifetime_emitter_suppresses_when_post_is_null()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);

            var handler = LifecycleEmitters.CreateMaxLifetimeEmitter(
                getLifecyclePost: () => null,
                agentConfig: BuildConfig(),
                agentStartTimeUtc: DateTime.UtcNow.AddMinutes(-360),
                logger: logger);

            var args = new EnrollmentTerminatedEventArgs(
                reason: EnrollmentTerminationReason.MaxLifetimeExceeded,
                outcome: EnrollmentTerminationOutcome.Failed,
                stageName: "AccountSetup",
                terminatedAtUtc: DateTime.UtcNow);

            // Should not throw — silently suppressed when ingress isn't ready.
            handler(sender: null, e: args);
        }

        [Fact]
        public void MaxLifetime_emitter_does_not_emit_for_non_max_lifetime_reason()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var sink = new FakeSignalIngressSink();
            var post = new InformationalEventPost(sink, SystemClock.Instance, logger);

            var handler = LifecycleEmitters.CreateMaxLifetimeEmitter(
                getLifecyclePost: () => post,
                agentConfig: BuildConfig(),
                agentStartTimeUtc: DateTime.UtcNow.AddMinutes(-30),
                logger: logger);

            var args = new EnrollmentTerminatedEventArgs(
                reason: EnrollmentTerminationReason.DecisionTerminalStage,
                outcome: EnrollmentTerminationOutcome.Succeeded,
                stageName: "Complete",
                terminatedAtUtc: DateTime.UtcNow);

            handler(sender: null, e: args);

            Assert.Empty(sink.Posted);
        }

        [Fact]
        public void MaxLifetime_emitter_emits_enrollment_failed_for_max_lifetime_reason()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var sink = new FakeSignalIngressSink();
            var post = new InformationalEventPost(sink, SystemClock.Instance, logger);

            var handler = LifecycleEmitters.CreateMaxLifetimeEmitter(
                getLifecyclePost: () => post,
                agentConfig: BuildConfig(),
                agentStartTimeUtc: DateTime.UtcNow.AddMinutes(-360),
                logger: logger);

            var args = new EnrollmentTerminatedEventArgs(
                reason: EnrollmentTerminationReason.MaxLifetimeExceeded,
                outcome: EnrollmentTerminationOutcome.Failed,
                stageName: "AccountSetup",
                terminatedAtUtc: DateTime.UtcNow);

            handler(sender: null, e: args);

            // The InformationalEventPost projects the EnrollmentEvent into a single
            // InformationalEvent decision signal — payload has the event type.
            Assert.Single(sink.Posted);
            var posted = sink.Posted[0];
            Assert.Equal(DecisionSignalKind.InformationalEvent, posted.Kind);
            Assert.Equal("enrollment_failed", posted.Payload?[SignalPayloadKeys.EventType]);
        }

        // ============================================================ AuthThreshold watchdog

        [Fact]
        public void AuthThreshold_handler_calls_signalShutdown()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var sink = new FakeSignalIngressSink();
            var post = new InformationalEventPost(sink, SystemClock.Instance, logger);

            var shutdownCalled = false;
            var handler = LifecycleEmitters.CreateAuthThresholdHandler(
                getLifecyclePost: () => post,
                agentConfig: BuildConfig(),
                signalShutdown: () => shutdownCalled = true,
                logger: logger);

            var args = new AuthFailureThresholdEventArgs(
                consecutiveFailures: 3,
                firstFailureUtc: DateTime.UtcNow.AddMinutes(-2),
                lastOperation: "Telemetry",
                lastStatusCode: 401,
                reason: "consecutive_failures");

            handler(sender: null, e: args);

            Assert.True(shutdownCalled);
        }

        [Fact]
        public void AuthThreshold_handler_emits_agent_shutdown_event_when_post_ready()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var sink = new FakeSignalIngressSink();
            var post = new InformationalEventPost(sink, SystemClock.Instance, logger);

            var handler = LifecycleEmitters.CreateAuthThresholdHandler(
                getLifecyclePost: () => post,
                agentConfig: BuildConfig(),
                signalShutdown: () => { },
                logger: logger);

            var args = new AuthFailureThresholdEventArgs(
                consecutiveFailures: 3,
                firstFailureUtc: DateTime.UtcNow.AddMinutes(-2),
                lastOperation: "Telemetry",
                lastStatusCode: 401,
                reason: "consecutive_failures");

            handler(sender: null, e: args);

            Assert.Single(sink.Posted);
            Assert.Equal("agent_shutdown", sink.Posted[0].Payload?[SignalPayloadKeys.EventType]);
        }

        [Fact]
        public void AuthThreshold_handler_still_signals_shutdown_when_post_null()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);

            var shutdownCalled = false;
            var handler = LifecycleEmitters.CreateAuthThresholdHandler(
                getLifecyclePost: () => null,
                agentConfig: BuildConfig(),
                signalShutdown: () => shutdownCalled = true,
                logger: logger);

            var args = new AuthFailureThresholdEventArgs(
                consecutiveFailures: 3,
                firstFailureUtc: DateTime.UtcNow,
                lastOperation: "Telemetry",
                lastStatusCode: 401,
                reason: "consecutive_failures");

            handler(sender: null, e: args);

            // Even if telemetry surface is gone, the shutdown signal MUST still fire.
            Assert.True(shutdownCalled);
        }

        // ============================================================ UnrestrictedMode no-op gate

        [Fact]
        public void UnrestrictedMode_audit_no_op_when_mergeResult_is_null()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var sink = new FakeSignalIngressSink();
            var post = new InformationalEventPost(sink, SystemClock.Instance, logger);

            LifecycleEmitters.EmitUnrestrictedModeAuditIfChanged(post, BuildConfig(), mergeResult: null, logger);

            Assert.Empty(sink.Posted);
        }

        [Fact]
        public void UnrestrictedMode_audit_no_op_when_no_change()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var sink = new FakeSignalIngressSink();
            var post = new InformationalEventPost(sink, SystemClock.Instance, logger);

            var merge = new RemoteConfigMergeResult { UnrestrictedModeChanged = false };

            LifecycleEmitters.EmitUnrestrictedModeAuditIfChanged(post, BuildConfig(), merge, logger);

            Assert.Empty(sink.Posted);
        }

        [Fact]
        public void UnrestrictedMode_audit_emits_when_changed()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var sink = new FakeSignalIngressSink();
            var post = new InformationalEventPost(sink, SystemClock.Instance, logger);

            var merge = new RemoteConfigMergeResult
            {
                UnrestrictedModeChanged = true,
                OldUnrestrictedMode = false,
                NewUnrestrictedMode = true,
            };

            LifecycleEmitters.EmitUnrestrictedModeAuditIfChanged(post, BuildConfig(), merge, logger);

            Assert.Single(sink.Posted);
            Assert.Equal("agent_unrestricted_mode_changed", sink.Posted[0].Payload?[SignalPayloadKeys.EventType]);
        }
    }
}
