using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// Single-rail refactor plan §4.3 — helper that assembles the InformationalEvent signal
    /// payload and posts to the decision-engine ingress. Callers avoid having to know the
    /// signal kind, the payload key strings, or the evidence shape.
    /// </summary>
    public sealed class InformationalEventPostTests
    {
        private static readonly DateTime At = new DateTime(2026, 4, 23, 10, 0, 0, DateTimeKind.Utc);

        private static (InformationalEventPost sut, FakeSignalIngressSink sink, VirtualClock clock) BuildRig()
        {
            var sink = new FakeSignalIngressSink();
            var clock = new VirtualClock(At);
            var sut = new InformationalEventPost(sink, clock);
            return (sut, sink, clock);
        }

        [Fact]
        public void Emit_posts_InformationalEvent_signal_with_eventType_and_source_in_payload()
        {
            var (sut, sink, _) = BuildRig();

            sut.Emit(eventType: "ntp_time_check", source: "Network");

            var posted = Assert.Single(sink.Posted);
            Assert.Equal(DecisionSignalKind.InformationalEvent, posted.Kind);
            Assert.Equal(1, posted.KindSchemaVersion);
            Assert.NotNull(posted.Payload);
            Assert.Equal("ntp_time_check", posted.Payload![SignalPayloadKeys.EventType]);
            Assert.Equal("Network", posted.Payload[SignalPayloadKeys.Source]);
        }

        [Fact]
        public void Emit_uses_clock_UtcNow_when_occurredAtUtc_omitted()
        {
            var (sut, sink, _) = BuildRig();

            sut.Emit("ntp_time_check", "Network");

            Assert.Equal(At, sink.Posted[0].OccurredAtUtc);
        }

        [Fact]
        public void Emit_honours_explicit_occurredAtUtc_override()
        {
            var (sut, sink, _) = BuildRig();
            var explicitTime = new DateTime(2026, 4, 23, 10, 5, 0, DateTimeKind.Utc);

            sut.Emit("ntp_time_check", "Network", occurredAtUtc: explicitTime);

            Assert.Equal(explicitTime, sink.Posted[0].OccurredAtUtc);
        }

        [Fact]
        public void Emit_defaults_sourceOrigin_to_source_label()
        {
            var (sut, sink, _) = BuildRig();

            sut.Emit("ntp_time_check", "Network");

            Assert.Equal("Network", sink.Posted[0].SourceOrigin);
        }

        [Fact]
        public void Emit_honours_explicit_sourceOrigin()
        {
            var (sut, sink, _) = BuildRig();

            sut.Emit(
                "ntp_time_check",
                "Network",
                sourceOrigin: "StartupEnvironmentProbes.Ntp");

            Assert.Equal("StartupEnvironmentProbes.Ntp", sink.Posted[0].SourceOrigin);
        }

        [Fact]
        public void Emit_optional_payload_keys_are_set_only_when_caller_provides_them()
        {
            var (sut, sink, _) = BuildRig();

            sut.Emit("ntp_time_check", "Network");

            var payload = sink.Posted[0].Payload!;
            Assert.False(payload.ContainsKey(SignalPayloadKeys.Message));
            Assert.False(payload.ContainsKey(SignalPayloadKeys.Severity));
            Assert.False(payload.ContainsKey(SignalPayloadKeys.ImmediateUpload));
            Assert.False(payload.ContainsKey("phase"));
        }

        [Fact]
        public void Emit_writes_optional_overrides_into_payload()
        {
            var (sut, sink, _) = BuildRig();
            var data = new Dictionary<string, string>
            {
                ["offsetSeconds"] = "-124.02",
                ["ntpServer"] = "time.windows.com",
            };

            sut.Emit(
                eventType: "ntp_time_check",
                source: "Network",
                message: "NTP offset -124.02s",
                severity: EventSeverity.Warning,
                immediateUpload: true,
                phase: EnrollmentPhase.DeviceSetup,
                data: data);

            var payload = sink.Posted[0].Payload!;
            Assert.Equal("NTP offset -124.02s", payload[SignalPayloadKeys.Message]);
            Assert.Equal("Warning", payload[SignalPayloadKeys.Severity]);
            Assert.Equal("true", payload[SignalPayloadKeys.ImmediateUpload]);
            Assert.Equal("DeviceSetup", payload["phase"]);
            Assert.Equal("-124.02", payload["offsetSeconds"]);
            Assert.Equal("time.windows.com", payload["ntpServer"]);
        }

        [Fact]
        public void ImmediateUpload_false_does_not_add_key_to_payload()
        {
            // Keeps the wire format minimal and matches the emitter's default (true when key is absent
            // is handled by emitter / not contradicted here; but the helper interprets "immediateUpload:false"
            // as "use default" — it is a passthrough convenience).
            var (sut, sink, _) = BuildRig();

            sut.Emit("agent_metrics_snapshot", "AgentSelfMetricsCollector", immediateUpload: false);

            Assert.False(sink.Posted[0].Payload!.ContainsKey(SignalPayloadKeys.ImmediateUpload));
        }

        [Fact]
        public void Data_cannot_override_reserved_top_level_keys()
        {
            // A caller passing "source" or "severity" inside the data dict must not rewrite the
            // explicit top-level values. The helper silently drops colliding data entries so the
            // on-wire payload stays unambiguous.
            var (sut, sink, _) = BuildRig();
            var data = new Dictionary<string, string>
            {
                [SignalPayloadKeys.Source] = "NOT_ALLOWED",
                [SignalPayloadKeys.Severity] = "NOT_ALLOWED",
                ["offsetSeconds"] = "-124.02",
            };

            sut.Emit(
                "ntp_time_check",
                source: "Network",
                severity: EventSeverity.Warning,
                data: data);

            var payload = sink.Posted[0].Payload!;
            Assert.Equal("Network", payload[SignalPayloadKeys.Source]);
            Assert.Equal("Warning", payload[SignalPayloadKeys.Severity]);
            Assert.Equal("-124.02", payload["offsetSeconds"]);
        }

        [Fact]
        public void Evidence_summary_prefers_explicit_argument_then_message_then_eventType()
        {
            var (sut, sink, _) = BuildRig();

            sut.Emit("ntp_time_check", "Network", evidenceSummary: "explicit");
            Assert.Equal("explicit", sink.Posted.Last().Evidence.Summary);

            sut.Emit("ntp_time_check", "Network", message: "fallback_message");
            Assert.Equal("fallback_message", sink.Posted.Last().Evidence.Summary);

            sut.Emit("ntp_time_check", "Network");
            Assert.Equal("ntp_time_check", sink.Posted.Last().Evidence.Summary);
        }

        [Fact]
        public void Empty_eventType_throws()
        {
            var (sut, _, _) = BuildRig();

            Assert.Throws<ArgumentException>(() => sut.Emit(eventType: "", source: "Network"));
            Assert.Throws<ArgumentException>(() => sut.Emit(eventType: null!, source: "Network"));
        }

        [Fact]
        public void Empty_source_throws()
        {
            var (sut, _, _) = BuildRig();

            Assert.Throws<ArgumentException>(() => sut.Emit(eventType: "x", source: ""));
            Assert.Throws<ArgumentException>(() => sut.Emit(eventType: "x", source: null!));
        }

        [Fact]
        public void Null_constructor_arguments_throw()
        {
            Assert.Throws<ArgumentNullException>(() => new InformationalEventPost(null!, new VirtualClock(At)));
            Assert.Throws<ArgumentNullException>(() => new InformationalEventPost(new FakeSignalIngressSink(), null!));
        }

        // ------------------------------------------------------ Emit(EnrollmentEvent) overload

        [Fact]
        public void Emit_EnrollmentEvent_overload_decomposes_all_top_level_fields_into_payload()
        {
            var (sut, sink, _) = BuildRig();
            var explicitTime = new DateTime(2026, 4, 23, 10, 5, 0, DateTimeKind.Utc);
            var evt = new EnrollmentEvent
            {
                EventType = "agent_started",
                Source = "AutopilotMonitor.Agent.V2",
                Severity = EventSeverity.Info,
                Phase = EnrollmentPhase.Unknown,
                Message = "Agent started",
                Timestamp = explicitTime,
                ImmediateUpload = true,
                Data = new Dictionary<string, object>
                {
                    ["agentVersion"] = "2.0.200",
                    ["awaitEnrollment"] = true,
                    ["agentMaxLifetimeMinutes"] = 360,
                },
            };

            sut.Emit(evt);

            var posted = Assert.Single(sink.Posted);
            var payload = posted.Payload!;
            Assert.Equal("agent_started", payload[SignalPayloadKeys.EventType]);
            Assert.Equal("AutopilotMonitor.Agent.V2", payload[SignalPayloadKeys.Source]);
            Assert.Equal("Agent started", payload[SignalPayloadKeys.Message]);
            Assert.Equal("Info", payload[SignalPayloadKeys.Severity]);
            Assert.Equal("true", payload[SignalPayloadKeys.ImmediateUpload]);
            // Data values are flattened to invariant strings so the wire shape stays deterministic.
            Assert.Equal("2.0.200", payload["agentVersion"]);
            Assert.Equal("True", payload["awaitEnrollment"]);
            Assert.Equal("360", payload["agentMaxLifetimeMinutes"]);
            // Phase=Unknown is an absent-by-default signal — helper does not leak it into payload.
            Assert.False(payload.ContainsKey("phase"));
            Assert.Equal(explicitTime, posted.OccurredAtUtc);
        }

        [Fact]
        public void Emit_EnrollmentEvent_overload_preserves_non_Unknown_phase()
        {
            var (sut, sink, _) = BuildRig();
            var evt = new EnrollmentEvent
            {
                EventType = "esp_phase_changed",
                Source = "EspAndHelloTracker",
                Phase = EnrollmentPhase.DeviceSetup,
                Data = new Dictionary<string, object>(),
            };

            sut.Emit(evt);

            Assert.Equal("DeviceSetup", sink.Posted[0].Payload!["phase"]);
        }

        [Fact]
        public void Emit_EnrollmentEvent_overload_forwards_ctor_supplied_Timestamp_as_utc()
        {
            // EnrollmentEvent()'s ctor sets Timestamp = DateTime.UtcNow unconditionally, so
            // there is no "default Timestamp" path — the helper forwards whatever the ctor set,
            // interpreting Unspecified kind as UTC (wire contract is always UTC).
            var (sut, sink, _) = BuildRig();
            var explicitTime = new DateTime(2026, 4, 23, 9, 30, 0, DateTimeKind.Unspecified);
            var evt = new EnrollmentEvent
            {
                EventType = "agent_started",
                Source = "AutopilotMonitor.Agent.V2",
                Timestamp = explicitTime,
                Data = new Dictionary<string, object>(),
            };

            sut.Emit(evt);

            Assert.Equal(DateTimeKind.Utc, sink.Posted[0].OccurredAtUtc.Kind);
            Assert.Equal(explicitTime.Ticks, sink.Posted[0].OccurredAtUtc.Ticks);
        }

        [Fact]
        public void Emit_EnrollmentEvent_overload_omits_empty_message_from_payload()
        {
            var (sut, sink, _) = BuildRig();
            var evt = new EnrollmentEvent
            {
                EventType = "agent_started",
                Source = "AutopilotMonitor.Agent.V2",
                Message = string.Empty,
                Data = new Dictionary<string, object>(),
            };

            sut.Emit(evt);

            Assert.False(sink.Posted[0].Payload!.ContainsKey(SignalPayloadKeys.Message));
        }

        [Fact]
        public void Emit_EnrollmentEvent_overload_stringifies_DateTime_values_with_roundtrip_format()
        {
            var (sut, sink, _) = BuildRig();
            var bootTime = new DateTime(2026, 4, 23, 8, 0, 0, DateTimeKind.Utc);
            var evt = new EnrollmentEvent
            {
                EventType = "agent_started",
                Source = "AutopilotMonitor.Agent.V2",
                Data = new Dictionary<string, object>
                {
                    ["previousBootUtc"] = bootTime,
                },
            };

            sut.Emit(evt);

            // "o" roundtrip format — deterministic, parseable back to the same DateTime.
            Assert.Equal("2026-04-23T08:00:00.0000000Z", sink.Posted[0].Payload!["previousBootUtc"]);
        }

        [Fact]
        public void Emit_EnrollmentEvent_overload_rejects_null_argument()
        {
            var (sut, _, _) = BuildRig();
            Assert.Throws<ArgumentNullException>(() => sut.Emit((EnrollmentEvent)null!));
        }
    }
}
