#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Program
{
    /// <summary>
    /// Regression gate for the "soft admin-action" semantics change: backend
    /// <c>AdminAction=Succeeded</c> is purely informational (timeline entry only, agent keeps
    /// running its own path) while <c>AdminAction=Failed</c> drives the agent's own soft-shutdown
    /// pipeline via the injected <c>onAdminFailed</c> callback — without forcing self-destruct.
    /// Previously both were routed through the full <c>ServerActionDispatcher</c> +
    /// <c>terminationHandler.Handle</c> path, which (a) hijacked the agent's own completion flow
    /// for Succeeded and (b) crashed with <c>InvalidOperationException: SignalIngress has been
    /// stopped</c> when a terminal-drain response arrived after the ingress was torn down.
    /// </summary>
    public sealed class HandleAdminActionTests
    {
        private static AgentConfiguration Config() => new AgentConfiguration
        {
            SessionId = "S1",
            TenantId = "T1",
            ApiBaseUrl = "http://localhost",
        };

        private sealed class Rig : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public AgentLogger Logger { get; }
            public FakeSignalIngressSink Ingress { get; } = new FakeSignalIngressSink();
            public InformationalEventPost Post { get; }
            public List<EnrollmentTerminatedEventArgs> OnAdminFailedCalls { get; } = new List<EnrollmentTerminatedEventArgs>();

            public Rig()
            {
                Logger = new AgentLogger(Path.Combine(Tmp.Path, "logs"), AgentLogLevel.Info);
                Post = new InformationalEventPost(Ingress, new VirtualClock(new DateTime(2026, 4, 23, 10, 0, 0, DateTimeKind.Utc)));
            }

            public Action<EnrollmentTerminatedEventArgs> OnAdminFailed => args => OnAdminFailedCalls.Add(args);

            public IEnumerable<FakeSignalIngressSink.PostedSignal> EventsOfType(string eventType) =>
                Ingress.Posted.Where(p =>
                    p.Kind == DecisionSignalKind.InformationalEvent &&
                    p.Payload != null &&
                    p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var t) &&
                    string.Equals(t, eventType, StringComparison.Ordinal));

            public void Dispose() => Tmp.Dispose();
        }

        [Fact]
        public void Succeeded_emits_admin_marked_session_as_Info_and_does_not_invoke_onAdminFailed()
        {
            using var rig = new Rig();

            global::AutopilotMonitor.Agent.V2.Program.HandleAdminAction(
                adminAction: "Succeeded",
                stageNameAccessor: () => "Completed",
                post: rig.Post,
                onAdminFailed: rig.OnAdminFailed,
                agentConfig: Config(),
                logger: rig.Logger);

            var emitted = rig.EventsOfType("admin_marked_session").ToList();
            Assert.Single(emitted);
            Assert.True(emitted[0].Payload!.TryGetValue(SignalPayloadKeys.Severity, out var sev));
            Assert.Equal(EventSeverity.Info.ToString(), sev);

            // Informational only — agent keeps running its own path.
            Assert.Empty(rig.OnAdminFailedCalls);

            // No server_action_* noise — Succeeded isn't a ServerAction.
            Assert.Empty(rig.EventsOfType("server_action_received"));
            Assert.Empty(rig.EventsOfType("server_action_executed"));
        }

        [Fact]
        public void Failed_emits_admin_marked_session_as_Warning_and_invokes_onAdminFailed_once()
        {
            using var rig = new Rig();

            global::AutopilotMonitor.Agent.V2.Program.HandleAdminAction(
                adminAction: "Failed",
                stageNameAccessor: () => "DeviceSetup",
                post: rig.Post,
                onAdminFailed: rig.OnAdminFailed,
                agentConfig: Config(),
                logger: rig.Logger);

            var emitted = rig.EventsOfType("admin_marked_session").ToList();
            Assert.Single(emitted);
            Assert.True(emitted[0].Payload!.TryGetValue(SignalPayloadKeys.Severity, out var sev));
            Assert.Equal(EventSeverity.Warning.ToString(), sev);

            Assert.Single(rig.OnAdminFailedCalls);
            var args = rig.OnAdminFailedCalls[0];
            Assert.Equal(EnrollmentTerminationOutcome.Failed, args.Outcome);
            Assert.Equal(EnrollmentTerminationReason.DecisionTerminalStage, args.Reason);
            Assert.Equal("DeviceSetup", args.StageName);
            Assert.Contains("Admin marked session as Failed", args.Details);
        }

        [Fact]
        public void Succeeded_is_case_insensitive()
        {
            using var rig = new Rig();

            global::AutopilotMonitor.Agent.V2.Program.HandleAdminAction(
                adminAction: "succeeded",
                stageNameAccessor: () => null,
                post: rig.Post,
                onAdminFailed: rig.OnAdminFailed,
                agentConfig: Config(),
                logger: rig.Logger);

            Assert.Empty(rig.OnAdminFailedCalls);
        }

        [Fact]
        public void Unknown_outcome_defaults_to_Failed_path()
        {
            // Forward-safety: any unknown adminAction string is treated as Failed — the agent's
            // soft-shutdown path is the safer default.
            using var rig = new Rig();

            global::AutopilotMonitor.Agent.V2.Program.HandleAdminAction(
                adminAction: "Quarantined",
                stageNameAccessor: () => null,
                post: rig.Post,
                onAdminFailed: rig.OnAdminFailed,
                agentConfig: Config(),
                logger: rig.Logger);

            Assert.Single(rig.OnAdminFailedCalls);
            Assert.Equal(EnrollmentTerminationOutcome.Failed, rig.OnAdminFailedCalls[0].Outcome);
        }

        [Fact]
        public void Failed_with_null_onAdminFailed_does_not_throw()
        {
            // Defensive path: onAdminFailed can be null if the terminate handler wasn't wired yet.
            using var rig = new Rig();

            var ex = Record.Exception(() =>
                global::AutopilotMonitor.Agent.V2.Program.HandleAdminAction(
                    adminAction: "Failed",
                    stageNameAccessor: () => null,
                    post: rig.Post,
                    onAdminFailed: null!,
                    agentConfig: Config(),
                    logger: rig.Logger));

            Assert.Null(ex);
            Assert.Single(rig.EventsOfType("admin_marked_session"));
        }

        [Fact]
        public void Failed_tolerates_stageNameAccessor_throwing()
        {
            // The stage accessor reads orchestrator.CurrentState, which throws
            // InvalidOperationException before Start. The handler must not rethrow — stageName
            // is a nice-to-have for telemetry, not a correctness dependency.
            using var rig = new Rig();

            global::AutopilotMonitor.Agent.V2.Program.HandleAdminAction(
                adminAction: "Failed",
                stageNameAccessor: () => throw new InvalidOperationException("not started"),
                post: rig.Post,
                onAdminFailed: rig.OnAdminFailed,
                agentConfig: Config(),
                logger: rig.Logger);

            Assert.Single(rig.OnAdminFailedCalls);
            Assert.Null(rig.OnAdminFailedCalls[0].StageName);
        }

        [Fact]
        public void Emit_suppresses_InvalidOperationException_from_stopped_ingress()
        {
            // Race regression gate: if the SignalIngress was stopped between the outer
            // shutdownComplete check and this Emit, the method must not surface the exception
            // to the transport's ServerResponseReceived handler — the original crash was
            // "SignalIngress has been stopped" caught upstream as an ERROR log.
            using var rig = new Rig();

            var stoppedPost = new InformationalEventPost(
                new ThrowingSink(),
                new VirtualClock(new DateTime(2026, 4, 23, 10, 0, 0, DateTimeKind.Utc)));

            var ex = Record.Exception(() =>
                global::AutopilotMonitor.Agent.V2.Program.HandleAdminAction(
                    adminAction: "Succeeded",
                    stageNameAccessor: () => null,
                    post: stoppedPost,
                    onAdminFailed: rig.OnAdminFailed,
                    agentConfig: Config(),
                    logger: rig.Logger));

            Assert.Null(ex);
            Assert.Empty(rig.OnAdminFailedCalls);
        }

        [Fact]
        public void Failed_emit_race_still_invokes_onAdminFailed()
        {
            // If the timeline emit fails due to a stopped ingress, the agent's own soft-shutdown
            // must still run — the admin's decision to Mark-Failed outranks a lost timeline entry.
            using var rig = new Rig();

            var stoppedPost = new InformationalEventPost(
                new ThrowingSink(),
                new VirtualClock(new DateTime(2026, 4, 23, 10, 0, 0, DateTimeKind.Utc)));

            global::AutopilotMonitor.Agent.V2.Program.HandleAdminAction(
                adminAction: "Failed",
                stageNameAccessor: () => "DeviceSetup",
                post: stoppedPost,
                onAdminFailed: rig.OnAdminFailed,
                agentConfig: Config(),
                logger: rig.Logger);

            Assert.Single(rig.OnAdminFailedCalls);
            Assert.Equal(EnrollmentTerminationOutcome.Failed, rig.OnAdminFailedCalls[0].Outcome);
        }

        private sealed class ThrowingSink : ISignalIngressSink
        {
            public void Post(
                DecisionSignalKind kind,
                DateTime occurredAtUtc,
                string sourceOrigin,
                Evidence evidence,
                IReadOnlyDictionary<string, string>? payload = null,
                int kindSchemaVersion = 1,
                object? typedPayload = null)
                => throw new InvalidOperationException("SignalIngress has been stopped.");
        }
    }
}
