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
            Assert.False(payload.ContainsKey("phase"));
            // ImmediateUpload is written both-ways (Finding 3) — default is "false". Keeping
            // the key present prevents the emitter from inferring "missing → true" and
            // silently overriding an explicit false value.
            Assert.Equal("false", payload[SignalPayloadKeys.ImmediateUpload]);
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
        public void ImmediateUpload_false_writes_explicit_false_key_to_payload()
        {
            // Codex Finding 3 — the previous "omit on false" behaviour combined with the
            // emitter's "missing → true" default silently flushed every performance_snapshot
            // and agent_metrics_snapshot as RequiresImmediateFlush=true. Helper now writes
            // the key both-ways so the emitter sees the caller's actual intent.
            var (sut, sink, _) = BuildRig();

            sut.Emit("agent_metrics_snapshot", "AgentSelfMetricsCollector", immediateUpload: false);

            Assert.Equal("false", sink.Posted[0].Payload![SignalPayloadKeys.ImmediateUpload]);
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

        [Theory]
        // Precedence: explicit evidenceSummary > message > eventType fallback. The helper uses
        // string.IsNullOrEmpty (not IsNullOrWhiteSpace), so a whitespace-only explicit value is
        // treated as "present" and passed through unchanged.
        [InlineData("explicit", "the_message", "ntp_time_check", "explicit")]
        [InlineData(null, "fallback_message", "ntp_time_check", "fallback_message")]
        [InlineData(null, null, "ntp_time_check", "ntp_time_check")]
        // Empty ("") evidenceSummary is treated as absent (IsNullOrEmpty) → fall through to message.
        [InlineData("", "fallback_message", "ntp_time_check", "fallback_message")]
        public void Evidence_summary_prefers_explicit_argument_then_message_then_eventType(
            string? evidenceSummary, string? message, string eventType, string expectedSummary)
        {
            var (sut, sink, _) = BuildRig();

            sut.Emit(eventType, "Network", message: message, evidenceSummary: evidenceSummary);

            Assert.Equal(expectedSummary, sink.Posted.Last().Evidence.Summary);
        }

        [Theory]
        // Empty / null eventType or source must be rejected eagerly — callers writing payloads
        // without either of these end up with a signal the reducer cannot interpret.
        [InlineData("", "Network")]
        [InlineData(null, "Network")]
        [InlineData("x", "")]
        [InlineData("x", null)]
        public void Emit_throws_on_empty_or_null_eventType_or_source(string? eventType, string? source)
        {
            var (sut, _, _) = BuildRig();

            Assert.Throws<ArgumentException>(() => sut.Emit(eventType: eventType!, source: source!));
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
            var data = new Dictionary<string, object>
            {
                ["agentVersion"] = "2.0.200",
                ["awaitEnrollment"] = true,
                ["agentMaxLifetimeMinutes"] = 360,
            };
            var evt = new EnrollmentEvent
            {
                EventType = "agent_started",
                Source = "Agent",
                Severity = EventSeverity.Info,
                Phase = EnrollmentPhase.Unknown,
                Message = "Agent started",
                Timestamp = explicitTime,
                ImmediateUpload = true,
                Data = data,
            };

            sut.Emit(evt);

            var posted = Assert.Single(sink.Posted);
            var payload = posted.Payload!;
            Assert.Equal("agent_started", payload[SignalPayloadKeys.EventType]);
            Assert.Equal("Agent", payload[SignalPayloadKeys.Source]);
            Assert.Equal("Agent started", payload[SignalPayloadKeys.Message]);
            Assert.Equal("Info", payload[SignalPayloadKeys.Severity]);
            // Finding 3: ImmediateUpload is written both-ways, not omitted when false.
            Assert.Equal("true", payload[SignalPayloadKeys.ImmediateUpload]);
            // Data values live on the typed sidecar — NOT duplicated in the string payload.
            // Keeps the decision-transitions log clean and avoids a stringify round-trip that
            // lost nested structure (Codex Finding 2).
            Assert.False(payload.ContainsKey("agentVersion"));
            Assert.False(payload.ContainsKey("awaitEnrollment"));
            Assert.False(payload.ContainsKey("agentMaxLifetimeMinutes"));
            Assert.Same(data, posted.TypedPayload);
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
                Source = "Agent",
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
                Source = "Agent",
                Message = string.Empty,
                Data = new Dictionary<string, object>(),
            };

            sut.Emit(evt);

            Assert.False(sink.Posted[0].Payload!.ContainsKey(SignalPayloadKeys.Message));
        }

        [Fact]
        public void Emit_EnrollmentEvent_overload_passes_DateTime_as_live_object_through_typed_payload()
        {
            // Typed sidecar preserves DateTime as a DateTime, not a stringified "o"-format
            // value. Newtonsoft serializes it to ISO-8601 on the outbound wire anyway (via
            // TelemetryEventEmitter.Emit → JsonConvert.SerializeObject), so the wire shape
            // is equivalent to the pre-single-rail direct emission.
            var (sut, sink, _) = BuildRig();
            var bootTime = new DateTime(2026, 4, 23, 8, 0, 0, DateTimeKind.Utc);
            var evt = new EnrollmentEvent
            {
                EventType = "agent_started",
                Source = "Agent",
                Data = new Dictionary<string, object>
                {
                    ["previousBootUtc"] = bootTime,
                },
            };

            sut.Emit(evt);

            var typed = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(sink.Posted[0].TypedPayload!);
            Assert.Equal(bootTime, Assert.IsType<DateTime>(typed["previousBootUtc"]));
        }

        [Fact]
        public void Emit_EnrollmentEvent_overload_rejects_null_argument()
        {
            var (sut, _, _) = BuildRig();
            Assert.Throws<ArgumentNullException>(() => sut.Emit((EnrollmentEvent)null!));
        }

        /// <summary>
        /// Codex Finding 2 — nested containers (Dictionary, List) must survive the bus with
        /// structure intact. The helper forwards <see cref="EnrollmentEvent.Data"/> through the
        /// typed sidecar (<c>DecisionSignal.TypedPayload</c>) untouched — no intermediate
        /// serialization. The emitter side then uses it directly as the final Data dict.
        /// </summary>
        [Fact]
        public void Emit_EnrollmentEvent_overload_forwards_Data_as_typed_payload_by_reference()
        {
            var (sut, sink, _) = BuildRig();
            var adapters = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    ["description"] = "Intel Wireless",
                    ["macAddress"] = "AA:BB:CC:DD:EE:FF",
                    ["dhcpEnabled"] = "True",
                },
                new Dictionary<string, object>
                {
                    ["description"] = "Loopback",
                    ["macAddress"] = null!,
                },
            };
            var data = new Dictionary<string, object>
            {
                ["adapterCount"] = 2,
                ["adapters"] = adapters,
            };
            var evt = new EnrollmentEvent
            {
                EventType = "network_adapters",
                Source = "DeviceInfoCollector",
                Data = data,
            };

            sut.Emit(evt);

            var posted = Assert.Single(sink.Posted);
            // typedPayload is the Data dict by reference — no copy, no stringify.
            Assert.Same(data, posted.TypedPayload);

            // The string payload does NOT duplicate Data keys (that would be a round-trip).
            var payload = posted.Payload!;
            Assert.False(payload.ContainsKey("adapterCount"));
            Assert.False(payload.ContainsKey("adapters"));
            // But top-level reserved fields are still present as strings so the reducer /
            // emitter can parse eventType / source / severity / message / immediateUpload.
            Assert.Equal("network_adapters", payload[SignalPayloadKeys.EventType]);
            Assert.Equal("DeviceInfoCollector", payload[SignalPayloadKeys.Source]);
        }

        [Fact]
        public void Emit_EnrollmentEvent_overload_forwards_null_Data_as_null_typed_payload()
        {
            var (sut, sink, _) = BuildRig();
            var evt = new EnrollmentEvent
            {
                EventType = "agent_started",
                Source = "Agent",
                Data = null!,
            };

            sut.Emit(evt);

            Assert.Null(sink.Posted[0].TypedPayload);
        }
    }
}
