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
