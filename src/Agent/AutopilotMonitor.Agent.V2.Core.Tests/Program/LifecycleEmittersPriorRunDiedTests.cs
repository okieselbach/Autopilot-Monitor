using System;
using System.Collections.Generic;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.Agent.V2.Runtime;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;
using SharedConstants = AutopilotMonitor.Shared.Constants;
using V2 = AutopilotMonitor.Agent.V2;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Program
{
    /// <summary>
    /// Tests for <see cref="LifecycleEmitters.PostPriorRunDiedWithState"/> — the Plan §B
    /// Death-Rattle helper. Asserts wire-payload shape (priorState dict + previousExitType
    /// + lastBootUtc + optional crashException), the defensive ExitType-null fallback, and
    /// the wire-policy contract (Severity=Warning, ImmediateUpload=true). The Plan §A
    /// snapshot-anchor coupling is exercised in
    /// <see cref="Telemetry.Events.EventTimelineEmitterDecisionStateEnrichmentTests"/>.
    /// </summary>
    public sealed class LifecycleEmittersPriorRunDiedTests
    {
        private static AgentLogger NewLogger(string path)
            => new AgentLogger(Path.Combine(path, "logs"), AgentLogLevel.Info);

        private static AgentConfiguration BuildConfig() => new AgentConfiguration
        {
            ApiBaseUrl = "https://example.invalid",
            SessionId = "S1",
            TenantId = "T1",
        };

        private static DecisionState BuildPriorState()
        {
            var b = DecisionState.CreateInitial("S1", "T1").ToBuilder()
                .WithStage(SessionStage.AwaitingHello)
                .WithStepIndex(73)
                .WithLastAppliedSignalOrdinal(72);
            b.DesktopArrivedUtc = new SignalFact<DateTime>(
                new DateTime(2026, 5, 1, 13, 45, 37, DateTimeKind.Utc),
                sourceSignalOrdinal: 28);
            return b.Build();
        }

        // ============================================================================
        // Happy path — event fires with the expected wire-payload shape
        // ============================================================================

        [Fact]
        public void Emits_a_single_prior_run_died_with_state_signal()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var sink = new FakeSignalIngressSink();
            var post = new InformationalEventPost(sink, SystemClock.Instance, logger);

            var previousExit = new V2.Program.PreviousExitSummary
            {
                ExitType = "reboot_kill",
                LastBootUtc = new DateTime(2026, 5, 1, 14, 0, 0, DateTimeKind.Utc),
            };

            LifecycleEmitters.PostPriorRunDiedWithState(post, BuildConfig(), previousExit, BuildPriorState(), logger);

            Assert.Single(sink.Posted);
            Assert.Equal(DecisionSignalKind.InformationalEvent, sink.Posted[0].Kind);
            Assert.Equal(
                SharedConstants.EventTypes.PriorRunDiedWithState,
                sink.Posted[0].Payload?[SignalPayloadKeys.EventType]);
        }

        [Fact]
        public void Wire_severity_is_Warning_and_immediateUpload_is_true()
        {
            // Hard-pin the wire-policy contract — death-rattle is operationally important
            // enough to warrant Warning (Inspector banner) + immediate upload (skip the
            // batched drain — it is the only surviving evidence of the dying run).
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var sink = new FakeSignalIngressSink();
            var post = new InformationalEventPost(sink, SystemClock.Instance, logger);

            var previousExit = new V2.Program.PreviousExitSummary
            {
                ExitType = "hard_kill",
                LastBootUtc = new DateTime(2026, 5, 1, 13, 0, 0, DateTimeKind.Utc),
            };

            LifecycleEmitters.PostPriorRunDiedWithState(post, BuildConfig(), previousExit, BuildPriorState(), logger);

            var payload = sink.Posted[0].Payload!;
            Assert.Equal("Warning", payload[SignalPayloadKeys.Severity]);
            Assert.Equal("true", payload[SignalPayloadKeys.ImmediateUpload]);
        }

        [Fact]
        public void TypedPayload_carries_priorState_previousExitType_and_lastBootUtc()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var sink = new FakeSignalIngressSink();
            var post = new InformationalEventPost(sink, SystemClock.Instance, logger);

            var lastBoot = new DateTime(2026, 5, 1, 14, 0, 0, DateTimeKind.Utc);
            var previousExit = new V2.Program.PreviousExitSummary
            {
                ExitType = "reboot_kill",
                LastBootUtc = lastBoot,
            };

            LifecycleEmitters.PostPriorRunDiedWithState(post, BuildConfig(), previousExit, BuildPriorState(), logger);

            // InformationalEventPost.Emit(EnrollmentEvent) routes evt.Data through the
            // typedPayload sidecar (plan §1.3) — nested dict / list values reach the
            // EventTimelineEmitter with structure intact. Assert the dict shape directly.
            var typed = Assert.IsAssignableFrom<IDictionary<string, object>>(sink.Posted[0].TypedPayload);
            Assert.Equal("reboot_kill", typed["previousExitType"]);
            Assert.Equal(lastBoot.ToString("o"), typed["lastBootUtc"]);
            Assert.IsAssignableFrom<IDictionary<string, object?>>(typed["priorState"]);
        }

        [Fact]
        public void PriorState_in_typedPayload_carries_snapshot_schema()
        {
            // The priorState dict embedded in Data must be the DecisionStateSnapshotBuilder
            // output (Plan §A schema) so the consumer (Inspector) can render it the same
            // way it renders a fresh-run anchor's data.decisionState.
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var sink = new FakeSignalIngressSink();
            var post = new InformationalEventPost(sink, SystemClock.Instance, logger);

            LifecycleEmitters.PostPriorRunDiedWithState(
                post,
                BuildConfig(),
                new V2.Program.PreviousExitSummary { ExitType = "reboot_kill" },
                BuildPriorState(),
                logger);

            var typed = (IDictionary<string, object>)sink.Posted[0].TypedPayload!;
            var priorState = Assert.IsAssignableFrom<IDictionary<string, object?>>(typed["priorState"]);

            Assert.Equal(DecisionStateSnapshotBuilder.SchemaVersion, priorState["schemaVersion"]);
            Assert.Equal(SessionStage.AwaitingHello.ToString(), priorState["stage"]);
            Assert.Equal(73, priorState["stepIndex"]);
            Assert.Equal(72L, priorState["lastAppliedSignalOrdinal"]);
        }

        [Fact]
        public void Emit_includes_crashExceptionType_when_exit_was_exception_crash()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var sink = new FakeSignalIngressSink();
            var post = new InformationalEventPost(sink, SystemClock.Instance, logger);

            var previousExit = new V2.Program.PreviousExitSummary
            {
                ExitType = "exception_crash",
                CrashExceptionType = "System.NullReferenceException",
            };

            LifecycleEmitters.PostPriorRunDiedWithState(post, BuildConfig(), previousExit, BuildPriorState(), logger);

            var typed = (IDictionary<string, object>)sink.Posted[0].TypedPayload!;
            Assert.Equal("System.NullReferenceException", typed["previousCrashException"]);
        }

        [Fact]
        public void Emit_omits_crashExceptionType_when_not_an_exception_crash()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var sink = new FakeSignalIngressSink();
            var post = new InformationalEventPost(sink, SystemClock.Instance, logger);

            var previousExit = new V2.Program.PreviousExitSummary
            {
                ExitType = "reboot_kill",
                CrashExceptionType = null,
            };

            LifecycleEmitters.PostPriorRunDiedWithState(post, BuildConfig(), previousExit, BuildPriorState(), logger);

            var typed = (IDictionary<string, object>)sink.Posted[0].TypedPayload!;
            Assert.False(typed.ContainsKey("previousCrashException"));
        }

        [Fact]
        public void Message_contains_exitType_stage_and_stepIndex()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var sink = new FakeSignalIngressSink();
            var post = new InformationalEventPost(sink, SystemClock.Instance, logger);

            var previousExit = new V2.Program.PreviousExitSummary { ExitType = "reboot_kill" };

            LifecycleEmitters.PostPriorRunDiedWithState(post, BuildConfig(), previousExit, BuildPriorState(), logger);

            var message = sink.Posted[0].Payload![SignalPayloadKeys.Message];
            Assert.Contains("reboot_kill", message);
            Assert.Contains("AwaitingHello", message);
            Assert.Contains("73", message);
        }

        // ============================================================================
        // Defensive fallback — null ExitType becomes "unknown" on the wire
        // ============================================================================

        [Fact]
        public void Null_ExitType_falls_back_to_unknown_on_wire()
        {
            // The AgentRuntimeHost gate (IsUncleanExit) already screens null ExitType, so
            // this branch is defensive — but the helper's own contract still has to hold
            // independently, in case a future caller skips that gate.
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var sink = new FakeSignalIngressSink();
            var post = new InformationalEventPost(sink, SystemClock.Instance, logger);

            var previousExit = new V2.Program.PreviousExitSummary
            {
                ExitType = null,
                LastBootUtc = null,
            };

            LifecycleEmitters.PostPriorRunDiedWithState(post, BuildConfig(), previousExit, BuildPriorState(), logger);

            var typed = (IDictionary<string, object>)sink.Posted[0].TypedPayload!;
            Assert.Equal("unknown", typed["previousExitType"]);
            Assert.Equal(string.Empty, typed["lastBootUtc"]);
            // Message must also be safe to render — no "null" leakage.
            var message = sink.Posted[0].Payload![SignalPayloadKeys.Message];
            Assert.Contains("unknown", message);
            Assert.DoesNotContain("null", message, StringComparison.OrdinalIgnoreCase);
        }

        // ============================================================================
        // Resilience — exceptions don't bubble out
        // ============================================================================

        [Fact]
        public void Helper_swallows_ingress_exceptions_and_does_not_throw()
        {
            // Bug-amplification guard: a transient ingress failure (or any throw inside
            // the helper) must not take the agent_started → version_check → … chain
            // down. Production wires this helper into a try/catch in AgentRuntimeHost
            // anyway, but the helper's own swallow keeps the chain robust regardless.
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);

            var sink = new FakeSignalIngressSink
            {
                ThrowOnPost = new InvalidOperationException("synthetic ingress failure for test"),
            };
            var post = new InformationalEventPost(sink, SystemClock.Instance, logger);

            var previousExit = new V2.Program.PreviousExitSummary { ExitType = "reboot_kill" };

            // Should not throw.
            LifecycleEmitters.PostPriorRunDiedWithState(post, BuildConfig(), previousExit, BuildPriorState(), logger);

            // Sink received the post attempt but threw; nothing was captured.
            Assert.Empty(sink.Posted);
        }
    }
}
