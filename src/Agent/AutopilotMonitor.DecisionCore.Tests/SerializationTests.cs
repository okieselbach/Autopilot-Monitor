using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Serialization;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests
{
    public sealed class SerializationTests
    {
        [Fact]
        public void SignalSerializer_roundtrip_preservesAllFields()
        {
            var original = new DecisionSignal(
                sessionSignalOrdinal: 42,
                sessionTraceOrdinal: 99,
                kind: DecisionSignalKind.EspPhaseChanged,
                kindSchemaVersion: 1,
                occurredAtUtc: new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc),
                sourceOrigin: "EspAndHelloTracker",
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: "esp-phase-detector-v1",
                    summary: "AccountSetup observed in registry",
                    rawPointer: "blob://events/42",
                    derivationInputs: new Dictionary<string, string>
                    {
                        ["registryKey"] = "HKLM\\...Autopilot\\EspStatus",
                        ["rawValue"] = "2",
                    }),
                payload: new Dictionary<string, string>
                {
                    ["phase"] = "AccountSetup",
                });

            var json = SignalSerializer.Serialize(original);
            var roundtripped = SignalSerializer.Deserialize(json);

            Assert.Equal(original.SessionSignalOrdinal, roundtripped.SessionSignalOrdinal);
            Assert.Equal(original.SessionTraceOrdinal, roundtripped.SessionTraceOrdinal);
            Assert.Equal(original.Kind, roundtripped.Kind);
            Assert.Equal(original.KindSchemaVersion, roundtripped.KindSchemaVersion);
            Assert.Equal(original.OccurredAtUtc, roundtripped.OccurredAtUtc);
            Assert.Equal(DateTimeKind.Utc, roundtripped.OccurredAtUtc.Kind);
            Assert.Equal(original.SourceOrigin, roundtripped.SourceOrigin);
            Assert.Equal(original.Evidence.Kind, roundtripped.Evidence.Kind);
            Assert.Equal(original.Evidence.Identifier, roundtripped.Evidence.Identifier);
            Assert.Equal(original.Evidence.Summary, roundtripped.Evidence.Summary);
            Assert.Equal(original.Evidence.RawPointer, roundtripped.Evidence.RawPointer);
            Assert.Equal("HKLM\\...Autopilot\\EspStatus", roundtripped.Evidence.DerivationInputs!["registryKey"]);
            Assert.Equal("AccountSetup", roundtripped.Payload!["phase"]);
        }

        [Fact]
        public void SignalSerializer_Deserialize_MissingEvidence_throws()
        {
            var json = "{\"SessionSignalOrdinal\":0,\"Kind\":\"SessionStarted\",\"KindSchemaVersion\":1,\"OccurredAtUtc\":\"2026-04-20T10:00:00Z\",\"SourceOrigin\":\"test\"}";
            Assert.Throws<JsonSerializationException>(() => SignalSerializer.Deserialize(json));
        }

        /// <summary>
        /// Single-rail typed-sidecar (plan §1.3) — when a signal carries structured
        /// <see cref="EnrollmentEvent.Data"/> through <see cref="DecisionSignal.TypedPayload"/>,
        /// persistence must write it to disk and restore it on read with enough fidelity that
        /// the next <c>TelemetryEventEmitter.Emit</c> re-emits the original wire shape.
        /// Dictionary values come back as Newtonsoft <c>JValue</c>/<c>JArray</c>/<c>JObject</c>
        /// tokens — the same tokens Newtonsoft serializes identically on the outbound side.
        /// </summary>
        [Fact]
        public void SignalSerializer_roundtrip_preserves_TypedPayload_structure()
        {
            var original = new DecisionSignal(
                sessionSignalOrdinal: 7,
                sessionTraceOrdinal: 7,
                kind: DecisionSignalKind.InformationalEvent,
                kindSchemaVersion: 1,
                occurredAtUtc: new DateTime(2026, 4, 23, 10, 0, 0, DateTimeKind.Utc),
                sourceOrigin: "DeviceInfoCollector",
                evidence: new Evidence(
                    kind: EvidenceKind.Raw,
                    identifier: "informational_event:network_adapters",
                    summary: "Network adapters configuration"),
                payload: new Dictionary<string, string>
                {
                    ["eventType"] = "network_adapters",
                    ["source"] = "DeviceInfoCollector",
                },
                typedPayload: new Dictionary<string, object>
                {
                    ["adapterCount"] = 2,
                    ["adapters"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["description"] = "Intel Wireless",
                            ["macAddress"] = "AA:BB:CC:DD:EE:FF",
                        },
                        new Dictionary<string, object>
                        {
                            ["description"] = "Loopback",
                        },
                    },
                });

            var json = SignalSerializer.Serialize(original);
            var roundtripped = SignalSerializer.Deserialize(json);

            // TypedPayload comes back as Dictionary<string, object> with JToken values —
            // enough for EventTimelineEmitter.ResolveData to consume it as Data, and for
            // Newtonsoft to re-serialize it identically.
            var typed = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(roundtripped.TypedPayload!);
            // Scalar → JValue(Integer)
            var adapterCount = Assert.IsType<Newtonsoft.Json.Linq.JValue>(typed["adapterCount"]);
            Assert.Equal(2L, adapterCount.Value);
            // Nested list → JArray of JObjects.
            var adapters = Assert.IsType<Newtonsoft.Json.Linq.JArray>(typed["adapters"]);
            Assert.Equal(2, adapters.Count);
            Assert.Equal("Intel Wireless", (string?)adapters[0]["description"]);
            Assert.Equal("AA:BB:CC:DD:EE:FF", (string?)adapters[0]["macAddress"]);
            Assert.Equal("Loopback", (string?)adapters[1]["description"]);
        }

        [Fact]
        public void SignalSerializer_roundtrip_null_TypedPayload_stays_null()
        {
            var original = new DecisionSignal(
                sessionSignalOrdinal: 1,
                sessionTraceOrdinal: 1,
                kind: DecisionSignalKind.SessionStarted,
                kindSchemaVersion: 1,
                occurredAtUtc: new DateTime(2026, 4, 23, 10, 0, 0, DateTimeKind.Utc),
                sourceOrigin: "test",
                evidence: new Evidence(EvidenceKind.Synthetic, "session-start", "first signal"),
                payload: null,
                typedPayload: null);

            var roundtripped = SignalSerializer.Deserialize(SignalSerializer.Serialize(original));
            Assert.Null(roundtripped.TypedPayload);
        }

        /// <summary>
        /// Codex Pass-2 finding — top-level null values inside a TypedPayload dict MUST survive
        /// persistence as C# null, not degenerate into <c>string.Empty</c>. Realistic producers
        /// (e.g. <c>DeviceInfoCollector</c> writing <c>displayVersion</c> = null when the
        /// registry key is absent) rely on this for wire/replay parity: live emits
        /// <c>{"displayVersion":null}</c>, so replay must too. The pre-fix code coerced null
        /// tokens to "" at deserialize time, breaking single-rail determinism on replay.
        /// </summary>
        [Fact]
        public void SignalSerializer_roundtrip_null_value_in_TypedPayload_stays_null_not_empty_string()
        {
            var original = new DecisionSignal(
                sessionSignalOrdinal: 1,
                sessionTraceOrdinal: 1,
                kind: DecisionSignalKind.InformationalEvent,
                kindSchemaVersion: 1,
                occurredAtUtc: new DateTime(2026, 4, 23, 10, 0, 0, DateTimeKind.Utc),
                sourceOrigin: "DeviceInfoCollector",
                evidence: new Evidence(EvidenceKind.Raw, "informational_event:os_info", "OS information collected"),
                payload: new Dictionary<string, string>
                {
                    ["eventType"] = "os_info",
                    ["source"] = "DeviceInfoCollector",
                },
                typedPayload: new Dictionary<string, object>
                {
                    ["version"] = "10.0.26100.1",
                    // Realistic: GetOsDisplayVersion() returned null on this SKU — collector
                    // places it directly into Data as null rather than coercing to "".
                    ["displayVersion"] = null!,
                    ["edition"] = "Enterprise",
                });

            var json = SignalSerializer.Serialize(original);
            // On-disk representation must already be JSON null, not "".
            Assert.Contains("\"displayVersion\":null", json);

            var roundtripped = SignalSerializer.Deserialize(json);
            var typed = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(roundtripped.TypedPayload!);

            // Pass-2 regression: was "" before the fix.
            Assert.True(typed.ContainsKey("displayVersion"));
            Assert.Null(typed["displayVersion"]);

            // Wire-parity check — re-serialize the restored payload as JSON and confirm the
            // null shape is preserved. This is what the outbound TelemetryEventEmitter.Emit
            // chain sees when replay fires through EventTimelineEmitter.
            var reemittedDataJson = Newtonsoft.Json.JsonConvert.SerializeObject(typed);
            Assert.Contains("\"displayVersion\":null", reemittedDataJson);
            Assert.DoesNotContain("\"displayVersion\":\"\"", reemittedDataJson);

            // Sibling non-null values untouched.
            Assert.Equal("10.0.26100.1", ((Newtonsoft.Json.Linq.JValue)typed["version"]).Value);
            Assert.Equal("Enterprise", ((Newtonsoft.Json.Linq.JValue)typed["edition"]).Value);
        }

        [Fact]
        public void UnknownFallbackEnumConverter_unknownValue_mapsToFallback()
        {
            // An old backend reading a row produced by a newer DecisionCore that added a
            // new Stage value must not crash — it reads it as Unknown instead.
            var settings = DecisionCoreJsonSettings.Create();
            var json = "\"NeueUnbekannteStage\"";

            var result = JsonConvert.DeserializeObject<SessionStage>(json, settings);

            Assert.Equal(SessionStage.Unknown, result);
        }

        [Fact]
        public void UnknownFallbackEnumConverter_knownValue_roundtripsByName()
        {
            var settings = DecisionCoreJsonSettings.Create();
            var original = SessionStage.AwaitingHello;

            var json = JsonConvert.SerializeObject(original, settings);
            var roundtripped = JsonConvert.DeserializeObject<SessionStage>(json, settings);

            Assert.Contains("AwaitingHello", json);
            Assert.Equal(original, roundtripped);
        }

        [Fact]
        public void UnknownFallbackEnumConverter_legacyNumericValue_mapsIfDefined()
        {
            var settings = DecisionCoreJsonSettings.Create();
            // EnrollmentPhase.AccountSetup is integer value 4.
            var json = "4";

            var result = JsonConvert.DeserializeObject<EnrollmentPhase>(json, settings);

            Assert.Equal(EnrollmentPhase.AccountSetup, result);
        }

        [Fact]
        public void UnknownFallbackEnumConverter_outOfRangeNumeric_mapsToFallback()
        {
            var settings = DecisionCoreJsonSettings.Create();
            var json = "9999";

            var result = JsonConvert.DeserializeObject<HypothesisLevel>(json, settings);

            Assert.Equal(HypothesisLevel.Unknown, result);
        }
    }
}
