using AutopilotMonitor.Functions.Services.Indexing;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pure tests for <see cref="IndexReconcileEnvelopeFactory"/> (Plan §2.8, §M5.d).
/// Verifies the shape-only projection from primary record → queue envelope — the
/// consumer (M5.d.3) relies on these fields being copied verbatim.
/// </summary>
public class IndexReconcileEnvelopeFactoryTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    private static readonly DateTime Occurred =
        new(2026, 4, 21, 14, 7, 33, DateTimeKind.Utc);

    // ============================================================ FromSignal

    [Fact]
    public void FromSignal_copies_discriminators_and_back_ref()
    {
        var record = new SignalRecord
        {
            TenantId             = TenantId,
            SessionId            = SessionId,
            SessionSignalOrdinal = 42,
            SessionTraceOrdinal  = 117,
            Kind                 = "EspTerminalFailure",
            KindSchemaVersion    = 2,
            OccurredAtUtc        = Occurred,
            SourceOrigin         = "EspAndHelloTrackerAdapter",
            PayloadJson          = "{}",
        };

        var envelope = IndexReconcileEnvelopeFactory.FromSignal(record);

        Assert.Equal("1", envelope.EnvelopeVersion);
        Assert.Equal("Signal", envelope.SourceKind);
        Assert.Equal(TenantId, envelope.TenantId);
        Assert.Equal(SessionId, envelope.SessionId);
        Assert.Equal(Occurred, envelope.OccurredAtUtc);
        Assert.Equal(42L, envelope.SessionSignalOrdinal);
        Assert.Equal("EspTerminalFailure", envelope.SignalKind);
        Assert.Equal("EspAndHelloTrackerAdapter", envelope.SourceOrigin);

        // DecisionTransition-only fields stay null on Signal envelopes.
        Assert.Null(envelope.StepIndex);
        Assert.Null(envelope.FromStage);
        Assert.Null(envelope.ToStage);
        Assert.Null(envelope.Taken);
        Assert.Null(envelope.IsTerminal);
        Assert.Null(envelope.DeadEndReason);
        Assert.Null(envelope.ClassifierVerdictId);
        Assert.Null(envelope.ClassifierHypothesisLevel);
    }

    // ============================================================ FromDecisionTransition

    [Fact]
    public void FromDecisionTransition_taken_terminal_with_classifier_copies_all_discriminators()
    {
        var record = new DecisionTransitionRecord
        {
            TenantId                  = TenantId,
            SessionId                 = SessionId,
            StepIndex                 = 7,
            SessionTraceOrdinal       = 118,
            SignalOrdinalRef          = 42,
            OccurredAtUtc             = Occurred,
            Trigger                   = "EspExiting",
            FromStage                 = "EspInProgress",
            ToStage                   = "WhiteGloveSealed",
            Taken                     = true,
            DeadEndReason             = null,
            ReducerVersion            = "1.0.0",
            IsTerminal                = true,
            ClassifierVerdictId       = "whiteglove-sealing",
            ClassifierHypothesisLevel = "Strong",
            PayloadJson               = "{}",
        };

        var envelope = IndexReconcileEnvelopeFactory.FromDecisionTransition(record);

        Assert.Equal("1", envelope.EnvelopeVersion);
        Assert.Equal("DecisionTransition", envelope.SourceKind);
        Assert.Equal(TenantId, envelope.TenantId);
        Assert.Equal(SessionId, envelope.SessionId);
        Assert.Equal(Occurred, envelope.OccurredAtUtc);
        Assert.Equal(7, envelope.StepIndex);
        Assert.Equal("EspInProgress", envelope.FromStage);
        Assert.Equal("WhiteGloveSealed", envelope.ToStage);
        Assert.Equal(true, envelope.Taken);
        Assert.Equal(true, envelope.IsTerminal);
        Assert.Null(envelope.DeadEndReason);
        Assert.Equal("whiteglove-sealing", envelope.ClassifierVerdictId);
        Assert.Equal("Strong", envelope.ClassifierHypothesisLevel);

        // Signal-only fields stay null on DecisionTransition envelopes.
        Assert.Null(envelope.SessionSignalOrdinal);
        Assert.Null(envelope.SignalKind);
        Assert.Null(envelope.SourceOrigin);
    }

    [Fact]
    public void FromDecisionTransition_dead_end_carries_reason_without_classifier_fields()
    {
        var record = new DecisionTransitionRecord
        {
            TenantId       = TenantId,
            SessionId      = SessionId,
            StepIndex      = 3,
            OccurredAtUtc  = Occurred,
            Trigger        = "EspExiting",
            FromStage      = "EspInProgress",
            ToStage        = "EspInProgress",
            Taken          = false,
            DeadEndReason  = "hybrid_reboot_gate_blocking",
            ReducerVersion = "1.0.0",
            IsTerminal     = false,
            PayloadJson    = "{}",
        };

        var envelope = IndexReconcileEnvelopeFactory.FromDecisionTransition(record);

        Assert.Equal(false, envelope.Taken);
        Assert.Equal(false, envelope.IsTerminal);
        Assert.Equal("hybrid_reboot_gate_blocking", envelope.DeadEndReason);
        Assert.Null(envelope.ClassifierVerdictId);
        Assert.Null(envelope.ClassifierHypothesisLevel);
    }

    // ============================================================ BuildBatch

    [Fact]
    public void BuildBatch_preserves_input_order_and_kind_tags()
    {
        var signals = new List<SignalRecord>
        {
            new() { TenantId = TenantId, SessionId = SessionId, SessionSignalOrdinal = 1, Kind = "EspPhaseChanged", OccurredAtUtc = Occurred },
            new() { TenantId = TenantId, SessionId = SessionId, SessionSignalOrdinal = 2, Kind = "DesktopArrived",   OccurredAtUtc = Occurred },
        };
        var transitions = new List<DecisionTransitionRecord>
        {
            new() { TenantId = TenantId, SessionId = SessionId, StepIndex = 1, FromStage = "A", ToStage = "B", Taken = true, OccurredAtUtc = Occurred },
        };

        var envelopes = IndexReconcileEnvelopeFactory.BuildBatch(signals, transitions);

        Assert.Equal(3, envelopes.Count);
        Assert.Equal("Signal",             envelopes[0].SourceKind);
        Assert.Equal(1L,                   envelopes[0].SessionSignalOrdinal);
        Assert.Equal("Signal",             envelopes[1].SourceKind);
        Assert.Equal(2L,                   envelopes[1].SessionSignalOrdinal);
        Assert.Equal("DecisionTransition", envelopes[2].SourceKind);
        Assert.Equal(1,                    envelopes[2].StepIndex);
    }

    [Fact]
    public void BuildBatch_returns_empty_for_null_or_empty_inputs()
    {
        Assert.Empty(IndexReconcileEnvelopeFactory.BuildBatch(
            new List<SignalRecord>(), new List<DecisionTransitionRecord>()));
        Assert.Empty(IndexReconcileEnvelopeFactory.BuildBatch(null!, null!));
    }
}
