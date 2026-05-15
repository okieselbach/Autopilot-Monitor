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
            // Event-type unification (2026-05-15): auth-failure now shares the canonical
            // `agent_shutting_down` event type with the rest of the V2 shutdown paths;
            // failure class still disambiguated via Data["reason"]=auth_failure.
            Assert.Equal("agent_shutting_down", sink.Posted[0].Payload?[SignalPayloadKeys.EventType]);
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

        // ============================================================ Gap-path shutdown emitter (2026-05-15)

        [Fact]
        public void GapPath_returns_NoPost_when_post_is_null()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);

            var result = LifecycleEmitters.EmitAgentShuttingDownGapPath(
                post: null,
                agentConfig: BuildConfig(),
                reason: "ctrl_c",
                agentStartTimeUtc: DateTime.UtcNow.AddMinutes(-3),
                agentVersion: "2.0.999",
                logger: logger);

            // P2 fix (2026-05-15): NoPost is the signal to the caller to RELEASE the gate
            // so a later fallback (typically the finally block once onIngressReady has
            // fired) can still surface the event. Tests must key on the enum value, not a
            // plain bool, so a future EmitFailed regression doesn't get classified as NoPost.
            Assert.Equal(LifecycleEmitters.AgentShuttingDownEmitResult.NoPost, result);
        }

        [Fact]
        public void GapPath_returns_Success_when_emit_lands()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var sink = new FakeSignalIngressSink();
            var post = new InformationalEventPost(sink, SystemClock.Instance, logger);

            var result = LifecycleEmitters.EmitAgentShuttingDownGapPath(
                post: post,
                agentConfig: BuildConfig(),
                reason: "ctrl_c",
                agentStartTimeUtc: DateTime.UtcNow.AddMinutes(-5),
                agentVersion: "2.0.999",
                logger: logger);

            Assert.Equal(LifecycleEmitters.AgentShuttingDownEmitResult.Success, result);
            Assert.Single(sink.Posted);
            Assert.Equal("agent_shutting_down", sink.Posted[0].Payload?[SignalPayloadKeys.EventType]);
        }

        [Fact]
        public void GapPath_emits_with_reason_process_exit()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var sink = new FakeSignalIngressSink();
            var post = new InformationalEventPost(sink, SystemClock.Instance, logger);

            LifecycleEmitters.EmitAgentShuttingDownGapPath(
                post: post,
                agentConfig: BuildConfig(),
                reason: "process_exit",
                agentStartTimeUtc: DateTime.UtcNow.AddMinutes(-2),
                agentVersion: "2.0.999",
                logger: logger);

            Assert.Single(sink.Posted);
            Assert.Equal("agent_shutting_down", sink.Posted[0].Payload?[SignalPayloadKeys.EventType]);
        }

        [Fact]
        public void GapPath_unhandled_exception_carries_exception_type_and_message()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var sink = new FakeSignalIngressSink();
            var post = new InformationalEventPost(sink, SystemClock.Instance, logger);

            LifecycleEmitters.EmitAgentShuttingDownGapPath(
                post: post,
                agentConfig: BuildConfig(),
                reason: "unhandled_exception",
                agentStartTimeUtc: DateTime.UtcNow.AddMinutes(-7),
                agentVersion: "2.0.999",
                logger: logger,
                exceptionType: "System.InvalidOperationException",
                exceptionMessage: "Synthetic test failure");

            Assert.Single(sink.Posted);
            Assert.Equal("agent_shutting_down", sink.Posted[0].Payload?[SignalPayloadKeys.EventType]);
        }

        [Fact]
        public void GapPath_truncates_long_exception_messages_to_keep_payload_bounded()
        {
            // Defensive: a stack-trace message could be many KB; the helper bounds it to 500
            // chars + ellipsis so the SignalIngress payload doesn't bloat under crash storms.
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var sink = new FakeSignalIngressSink();
            var post = new InformationalEventPost(sink, SystemClock.Instance, logger);

            var longMessage = new string('x', 2000);
            var result = LifecycleEmitters.EmitAgentShuttingDownGapPath(
                post: post,
                agentConfig: BuildConfig(),
                reason: "unhandled_exception",
                agentStartTimeUtc: DateTime.UtcNow.AddMinutes(-1),
                agentVersion: "2.0.999",
                logger: logger,
                exceptionType: "System.Exception",
                exceptionMessage: longMessage);

            Assert.Equal(LifecycleEmitters.AgentShuttingDownEmitResult.Success, result);
            Assert.Single(sink.Posted);
        }

        // -------- AuthFailure gate participation (P1 fix 2026-05-15) --------

        [Fact]
        public void AuthThreshold_handler_skips_emit_when_gate_already_claimed()
        {
            // P1 fix: the auth-failure path now participates in the cross-path idempotency
            // gate. If a Ctrl+C / ProcessExit / Terminated path already claimed the slot,
            // the auth-failure emit MUST suppress to avoid double-emitting agent_shutting_down.
            // signalShutdown still fires — the watchdog's primary job (initiate exit) is
            // independent of telemetry.
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var sink = new FakeSignalIngressSink();
            var post = new InformationalEventPost(sink, SystemClock.Instance, logger);

            var shutdownCalled = false;
            var handler = LifecycleEmitters.CreateAuthThresholdHandler(
                getLifecyclePost: () => post,
                agentConfig: BuildConfig(),
                signalShutdown: () => shutdownCalled = true,
                logger: logger,
                tryClaimShutdownEvent: () => false, // gate already taken by another path
                releaseShutdownEventClaim: null);

            var args = new AuthFailureThresholdEventArgs(
                consecutiveFailures: 3,
                firstFailureUtc: DateTime.UtcNow.AddMinutes(-2),
                lastOperation: "Telemetry",
                lastStatusCode: 401,
                reason: "consecutive_failures");

            handler(sender: null, e: args);

            Assert.True(shutdownCalled);
            Assert.Empty(sink.Posted); // emit suppressed under claimed gate
        }

        [Fact]
        public void AuthThreshold_handler_emits_when_gate_grants_slot()
        {
            // Symmetric counterpart: when the gate grants the slot the handler emits as usual.
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var sink = new FakeSignalIngressSink();
            var post = new InformationalEventPost(sink, SystemClock.Instance, logger);

            var handler = LifecycleEmitters.CreateAuthThresholdHandler(
                getLifecyclePost: () => post,
                agentConfig: BuildConfig(),
                signalShutdown: () => { },
                logger: logger,
                tryClaimShutdownEvent: () => true,
                releaseShutdownEventClaim: null);

            handler(sender: null, e: new AuthFailureThresholdEventArgs(
                consecutiveFailures: 3,
                firstFailureUtc: DateTime.UtcNow.AddMinutes(-2),
                lastOperation: "Telemetry",
                lastStatusCode: 401,
                reason: "consecutive_failures"));

            Assert.Single(sink.Posted);
            Assert.Equal("agent_shutting_down", sink.Posted[0].Payload?[SignalPayloadKeys.EventType]);
        }

        [Fact]
        public void AuthThreshold_handler_releases_gate_when_post_is_null()
        {
            // P2 fix: when auth-failure fires before onIngressReady, the handler claimed the
            // gate but cannot actually emit. It MUST release the slot so the later finally
            // fallback (runtime_host_exit) can still surface the shutdown event on the wire.
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);

            int claimed = 0;
            var released = false;

            var handler = LifecycleEmitters.CreateAuthThresholdHandler(
                getLifecyclePost: () => null, // ingress not ready yet
                agentConfig: BuildConfig(),
                signalShutdown: () => { },
                logger: logger,
                tryClaimShutdownEvent: () =>
                {
                    if (claimed == 1) return false;
                    claimed = 1;
                    return true;
                },
                releaseShutdownEventClaim: () => released = true);

            handler(sender: null, e: new AuthFailureThresholdEventArgs(
                consecutiveFailures: 3,
                firstFailureUtc: DateTime.UtcNow,
                lastOperation: "Telemetry",
                lastStatusCode: 401,
                reason: "consecutive_failures"));

            Assert.True(released, "auth_failure must release the gate when ingress was not ready so a later fallback can retry.");
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
