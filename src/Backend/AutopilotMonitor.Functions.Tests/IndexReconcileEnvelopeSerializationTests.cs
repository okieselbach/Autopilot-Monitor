using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// JSON round-trip tests for <see cref="IndexReconcileEnvelope"/> (Plan §2.8, §M5.d).
/// The producer (M5.d.2) serializes with Newtonsoft; the queue-triggered consumer
/// (M5.d.3) must deserialize the same shape back — verified here so the contract
/// doesn't drift silently as fields are added.
/// </summary>
public class IndexReconcileEnvelopeSerializationTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    private static readonly DateTime Occurred =
        new(2026, 4, 21, 14, 7, 33, DateTimeKind.Utc);

    [Fact]
    public void Signal_envelope_round_trips_through_newtonsoft()
    {
        var original = new IndexReconcileEnvelope
        {
            EnvelopeVersion      = "1",
            SourceKind           = "Signal",
            TenantId             = TenantId,
            SessionId            = SessionId,
            OccurredAtUtc        = Occurred,
            SessionSignalOrdinal = 42,
            SignalKind           = "EspTerminalFailure",
            SourceOrigin         = "EspAndHelloTrackerAdapter",
        };

        var json = JsonConvert.SerializeObject(original);
        var restored = JsonConvert.DeserializeObject<IndexReconcileEnvelope>(json);

        Assert.NotNull(restored);
        Assert.Equal("1",                          restored!.EnvelopeVersion);
        Assert.Equal("Signal",                     restored.SourceKind);
        Assert.Equal(TenantId,                     restored.TenantId);
        Assert.Equal(SessionId,                    restored.SessionId);
        Assert.Equal(Occurred,                     restored.OccurredAtUtc.ToUniversalTime());
        Assert.Equal(42L,                          restored.SessionSignalOrdinal);
        Assert.Equal("EspTerminalFailure",         restored.SignalKind);
        Assert.Equal("EspAndHelloTrackerAdapter",  restored.SourceOrigin);
        Assert.Null(restored.StepIndex);
        Assert.Null(restored.ClassifierVerdictId);
    }

    [Fact]
    public void DecisionTransition_envelope_round_trips_all_discriminators()
    {
        var original = new IndexReconcileEnvelope
        {
            EnvelopeVersion           = "1",
            SourceKind                = "DecisionTransition",
            TenantId                  = TenantId,
            SessionId                 = SessionId,
            OccurredAtUtc             = Occurred,
            StepIndex                 = 7,
            FromStage                 = "EspInProgress",
            ToStage                   = "WhiteGloveSealed",
            Taken                     = true,
            IsTerminal                = true,
            DeadEndReason             = null,
            ClassifierVerdictId       = "whiteglove-sealing",
            ClassifierHypothesisLevel = "Strong",
        };

        var json = JsonConvert.SerializeObject(original);
        var restored = JsonConvert.DeserializeObject<IndexReconcileEnvelope>(json);

        Assert.NotNull(restored);
        Assert.Equal("DecisionTransition",  restored!.SourceKind);
        Assert.Equal(7,                     restored.StepIndex);
        Assert.Equal("EspInProgress",       restored.FromStage);
        Assert.Equal("WhiteGloveSealed",    restored.ToStage);
        Assert.Equal(true,                  restored.Taken);
        Assert.Equal(true,                  restored.IsTerminal);
        Assert.Null(restored.DeadEndReason);
        Assert.Equal("whiteglove-sealing",  restored.ClassifierVerdictId);
        Assert.Equal("Strong",              restored.ClassifierHypothesisLevel);
        Assert.Null(restored.SessionSignalOrdinal);
    }

    [Fact]
    public void Deserialization_tolerates_missing_optional_fields()
    {
        // A minimal envelope from a consumer-compatible-but-older writer should still parse.
        const string minimal =
            "{\"envelopeVersion\":\"1\",\"sourceKind\":\"Signal\",\"tenantId\":\"t\",\"sessionId\":\"s\",\"occurredAtUtc\":\"2026-04-21T14:07:33Z\"}";

        var restored = JsonConvert.DeserializeObject<IndexReconcileEnvelope>(minimal);

        Assert.NotNull(restored);
        Assert.Equal("Signal", restored!.SourceKind);
        Assert.Null(restored.SessionSignalOrdinal);
        Assert.Null(restored.SignalKind);
        Assert.Null(restored.StepIndex);
    }
}
