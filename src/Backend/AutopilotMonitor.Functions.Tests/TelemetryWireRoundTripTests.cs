using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Classifiers;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Functions.Functions.Ingest;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// <b>Cross-layer round-trip</b> (Codex Finding 1 follow-up gate): proves that a
/// <see cref="DecisionSignal"/> / <see cref="DecisionTransition"/> serialized on the V2 agent
/// deserializes correctly on the backend into a <see cref="SignalRecord"/> /
/// <see cref="DecisionTransitionRecord"/>. Failure here = the agent's uploaded Signals or
/// Transitions never reach the Inspector / index pipeline intact.
/// <para>
/// This test replicates the <c>JsonSerializerSettings</c> the agent-side emitters use
/// (Newtonsoft + <see cref="StringEnumConverter"/> + <see cref="NullValueHandling.Ignore"/>).
/// The backend project can't reference the net48 agent assembly directly (one-way cross-TFM
/// constraint), so we exercise the wire contract via the shared <c>DecisionCore</c> types
/// plus the agreed serialization settings. Any drift between agent and backend would break
/// this assertion immediately.
/// </para>
/// </summary>
public class TelemetryWireRoundTripTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    private static readonly DateTime At = new(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

    // Must mirror TelemetrySignalEmitter / TelemetryTransitionEmitter in V2.Core.
    private static readonly JsonSerializerSettings AgentWireFormat = new()
    {
        Converters = { new StringEnumConverter() },
        NullValueHandling = NullValueHandling.Ignore,
    };

    private static TelemetryItemDto DraftToDto(string kind, string payloadJson, long sessionTraceOrdinal) =>
        new TelemetryItemDto
        {
            Kind                = kind,
            PartitionKey        = $"{TenantId}_{SessionId}",
            RowKey              = "0",
            TelemetryItemId     = 1,
            SessionTraceOrdinal = sessionTraceOrdinal,
            PayloadJson         = payloadJson,
            EnqueuedAtUtc       = At,
            RetryCount          = 0,
        };

    // ============================================================ Signal wire round-trip

    [Fact]
    public void DecisionSignal_serialized_with_agent_settings_parses_into_SignalRecord()
    {
        var signal = new DecisionSignal(
            sessionSignalOrdinal: 42,
            sessionTraceOrdinal: 142,
            kind: DecisionSignalKind.EspTerminalFailure,
            kindSchemaVersion: 2,
            occurredAtUtc: At,
            sourceOrigin: "EspAndHelloTrackerAdapter",
            evidence: new Evidence(EvidenceKind.Raw, "raw-42", "evidence-42"));

        var payloadJson = JsonConvert.SerializeObject(signal, AgentWireFormat);
        var dto = DraftToDto("Signal", payloadJson, sessionTraceOrdinal: 142);

        var record = TelemetryPayloadParser.ParseSignal(dto, TenantId, SessionId);

        Assert.NotNull(record);
        Assert.Equal(42L,                         record!.SessionSignalOrdinal);
        Assert.Equal(142L,                        record.SessionTraceOrdinal);
        Assert.Equal("EspTerminalFailure",        record.Kind);
        Assert.Equal(2,                           record.KindSchemaVersion);
        Assert.Equal(At,                          record.OccurredAtUtc.ToUniversalTime());
        Assert.Equal("EspAndHelloTrackerAdapter", record.SourceOrigin);
        Assert.Equal(payloadJson,                 record.PayloadJson);
    }

    [Fact]
    public void DecisionSignal_with_explicit_ordinal_zero_round_trips()
    {
        // Defense against the Finding 3 regression path: ordinal 0 is a legal value and must
        // survive serialize → parse. (The parser's guard rejects *missing* ordinals, not
        // legitimate 0s.)
        var signal = new DecisionSignal(
            sessionSignalOrdinal: 0,
            sessionTraceOrdinal: 0,
            kind: DecisionSignalKind.SessionStarted,
            kindSchemaVersion: 1,
            occurredAtUtc: At,
            sourceOrigin: "SignalIngress",
            evidence: new Evidence(EvidenceKind.Raw, "s", "start"));

        var payloadJson = JsonConvert.SerializeObject(signal, AgentWireFormat);
        var record = TelemetryPayloadParser.ParseSignal(
            DraftToDto("Signal", payloadJson, 0), TenantId, SessionId);

        Assert.NotNull(record);
        Assert.Equal(0L, record!.SessionSignalOrdinal);
    }

    // ============================================================ DecisionTransition wire round-trip

    [Fact]
    public void DecisionTransition_Taken_terminal_with_classifier_round_trips_all_index_discriminators()
    {
        var verdict = new ClassifierVerdict(
            classifierId: "whiteglove-sealing",
            level: HypothesisLevel.Strong,
            score: 80,
            contributingFactors: new List<string> { "PatternMatched" },
            reason: "matched sealing pattern",
            inputHash: "abc123");

        var transition = new DecisionTransition(
            stepIndex: 7,
            sessionTraceOrdinal: 207,
            signalOrdinalRef: 42,
            occurredAtUtc: At,
            trigger: "EspExiting",
            fromStage: SessionStage.EspDeviceSetup,
            toStage: SessionStage.WhiteGloveSealed,
            taken: true,
            deadEndReason: null,
            reducerVersion: "1.0.0",
            classifierVerdict: verdict);

        var payloadJson = JsonConvert.SerializeObject(transition, AgentWireFormat);
        var dto = DraftToDto("DecisionTransition", payloadJson, sessionTraceOrdinal: 207);

        var record = TelemetryPayloadParser.ParseTransition(dto, TenantId, SessionId);

        Assert.NotNull(record);
        Assert.Equal(7,                     record!.StepIndex);
        Assert.Equal(207L,                  record.SessionTraceOrdinal);
        Assert.Equal(42L,                   record.SignalOrdinalRef);
        Assert.Equal("EspExiting",          record.Trigger);
        Assert.Equal("EspDeviceSetup",      record.FromStage);
        Assert.Equal("WhiteGloveSealed",    record.ToStage);
        Assert.True(record.Taken);
        Assert.True(record.IsTerminal);                                         // Projected from ToStage
        Assert.Equal("whiteglove-sealing",  record.ClassifierVerdictId);
        Assert.Equal("Strong",              record.ClassifierHypothesisLevel);  // StringEnumConverter → "Strong"
        Assert.Equal("1.0.0",               record.ReducerVersion);
        Assert.Equal(payloadJson,           record.PayloadJson);
    }

    [Fact]
    public void DecisionTransition_dead_end_preserves_DeadEndReason_and_drops_classifier_fields()
    {
        var blocked = new DecisionTransition(
            stepIndex: 3,
            sessionTraceOrdinal: 103,
            signalOrdinalRef: 5,
            occurredAtUtc: At,
            trigger: "EspExiting",
            fromStage: SessionStage.EspDeviceSetup,
            toStage: SessionStage.EspDeviceSetup,
            taken: false,
            deadEndReason: "hybrid_reboot_gate_blocking",
            reducerVersion: "1.0.0");

        var payloadJson = JsonConvert.SerializeObject(blocked, AgentWireFormat);
        var record = TelemetryPayloadParser.ParseTransition(
            DraftToDto("DecisionTransition", payloadJson, 103), TenantId, SessionId);

        Assert.NotNull(record);
        Assert.False(record!.Taken);
        Assert.Equal("hybrid_reboot_gate_blocking", record.DeadEndReason);
        Assert.False(record.IsTerminal);
        Assert.Null(record.ClassifierVerdictId);
        Assert.Null(record.ClassifierHypothesisLevel);
    }

    [Fact]
    public void DecisionTransition_with_step_zero_round_trips()
    {
        // StepIndex=0 is the first reducer step per session. Must survive serialize → parse
        // (Finding 3 guard rejects missing StepIndex, not legitimate 0).
        var first = new DecisionTransition(
            stepIndex: 0,
            sessionTraceOrdinal: 100,
            signalOrdinalRef: 0,
            occurredAtUtc: At,
            trigger: "SessionStarted",
            fromStage: SessionStage.Unknown,
            toStage: SessionStage.AwaitingEspPhaseChange,
            taken: true,
            deadEndReason: null,
            reducerVersion: "1.0.0");

        var payloadJson = JsonConvert.SerializeObject(first, AgentWireFormat);
        var record = TelemetryPayloadParser.ParseTransition(
            DraftToDto("DecisionTransition", payloadJson, 100), TenantId, SessionId);

        Assert.NotNull(record);
        Assert.Equal(0, record!.StepIndex);
        Assert.Equal("AwaitingEspPhaseChange", record.ToStage);
    }
}
