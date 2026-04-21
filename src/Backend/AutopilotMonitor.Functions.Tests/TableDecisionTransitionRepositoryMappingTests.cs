using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pure mapping tests for <see cref="TableDecisionTransitionRepository"/>. Sibling of
/// <see cref="TableSignalRepositoryMappingTests"/> — same rationale.
/// </summary>
public class TableDecisionTransitionRepositoryMappingTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    [Fact]
    public void BuildPartitionKey_is_tenant_underscore_session()
    {
        var pk = TableDecisionTransitionRepository.BuildPartitionKey(TenantId, SessionId);
        Assert.Equal($"{TenantId}_{SessionId}", pk);
    }

    [Fact]
    public void BuildRowKey_pads_stepIndex_to_10_digits()
    {
        Assert.Equal("0000000000", TableDecisionTransitionRepository.BuildRowKey(0));
        Assert.Equal("0000000042", TableDecisionTransitionRepository.BuildRowKey(42));
        Assert.Equal("2147483647", TableDecisionTransitionRepository.BuildRowKey(int.MaxValue));
    }

    [Fact]
    public void ToEntity_projects_all_typed_columns()
    {
        var record = new DecisionTransitionRecord
        {
            TenantId                    = TenantId,
            SessionId                   = SessionId,
            StepIndex                   = 7,
            SessionTraceOrdinal         = 117,
            SignalOrdinalRef            = 42,
            OccurredAtUtc               = new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc),
            Trigger                     = "EspExiting",
            FromStage                   = "EspInProgress",
            ToStage                     = "WhiteGloveSealed",
            Taken                       = true,
            DeadEndReason               = null,
            ReducerVersion              = "1.0.0",
            IsTerminal                  = true,
            ClassifierVerdictId         = "whiteglove-sealing",
            ClassifierHypothesisLevel   = "Strong",
            PayloadJson                 = "{\"foo\":\"bar\"}",
        };

        var entity = TableDecisionTransitionRepository.ToEntity(record);

        Assert.Equal($"{TenantId}_{SessionId}", entity.PartitionKey);
        Assert.Equal("0000000007", entity.RowKey);
        Assert.Equal(TenantId, entity.GetString("TenantId"));
        Assert.Equal(SessionId, entity.GetString("SessionId"));
        Assert.Equal(7, entity.GetInt32("StepIndex"));
        Assert.Equal(117L, entity.GetInt64("SessionTraceOrdinal"));
        Assert.Equal(42L, entity.GetInt64("SignalOrdinalRef"));
        Assert.Equal(record.OccurredAtUtc, entity.GetDateTime("OccurredAtUtc"));
        Assert.Equal("EspExiting", entity.GetString("Trigger"));
        Assert.Equal("EspInProgress", entity.GetString("FromStage"));
        Assert.Equal("WhiteGloveSealed", entity.GetString("ToStage"));
        Assert.Equal(true, entity.GetBoolean("Taken"));
        Assert.Null(entity.GetString("DeadEndReason"));
        Assert.Equal("1.0.0", entity.GetString("ReducerVersion"));
        Assert.Equal(true, entity.GetBoolean("IsTerminal"));
        Assert.Equal("whiteglove-sealing", entity.GetString("ClassifierVerdictId"));
        Assert.Equal("Strong", entity.GetString("ClassifierHypothesisLevel"));
        Assert.Equal("{\"foo\":\"bar\"}", entity.GetString("PayloadJson"));
    }

    [Fact]
    public void ToEntity_preserves_dead_end_reason_for_blocked_transitions()
    {
        var record = new DecisionTransitionRecord
        {
            TenantId            = TenantId,
            SessionId           = SessionId,
            StepIndex           = 3,
            SessionTraceOrdinal = 5,
            SignalOrdinalRef    = 3,
            OccurredAtUtc       = DateTime.UtcNow,
            Trigger             = "EspExiting",
            FromStage           = "EspInProgress",
            ToStage             = "EspInProgress", // no transition — dead end
            Taken               = false,
            DeadEndReason       = "hybrid_reboot_gate_blocking",
            ReducerVersion      = "1.0.0",
            IsTerminal          = false,
            PayloadJson         = "{}",
        };

        var entity = TableDecisionTransitionRepository.ToEntity(record);

        Assert.Equal(false, entity.GetBoolean("Taken"));
        Assert.Equal("hybrid_reboot_gate_blocking", entity.GetString("DeadEndReason"));
    }

    [Fact]
    public void ToEntity_chunks_oversized_PayloadJson()
    {
        var oversized = new string('x', 75_000);
        var record = new DecisionTransitionRecord
        {
            TenantId            = TenantId,
            SessionId           = SessionId,
            StepIndex           = 1,
            SessionTraceOrdinal = 1,
            SignalOrdinalRef    = 1,
            OccurredAtUtc       = DateTime.UtcNow,
            Trigger             = "t",
            FromStage           = "F",
            ToStage             = "T",
            Taken               = true,
            ReducerVersion      = "1.0.0",
            PayloadJson         = oversized,
        };

        var entity = TableDecisionTransitionRepository.ToEntity(record);

        Assert.Equal("3", entity.GetString("PayloadJson_ChunkCount"));
        Assert.False(entity.ContainsKey("PayloadJson"));
    }

    // ============================================================ Reverse mapping (read path)

    [Fact]
    public void FromEntity_round_trips_all_typed_columns()
    {
        var original = new DecisionTransitionRecord
        {
            TenantId                    = TenantId,
            SessionId                   = SessionId,
            StepIndex                   = 7,
            SessionTraceOrdinal         = 117,
            SignalOrdinalRef            = 42,
            OccurredAtUtc               = new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc),
            Trigger                     = "EspExiting",
            FromStage                   = "EspInProgress",
            ToStage                     = "WhiteGloveSealed",
            Taken                       = true,
            DeadEndReason               = null,
            ReducerVersion              = "1.0.0",
            IsTerminal                  = true,
            ClassifierVerdictId         = "whiteglove-sealing",
            ClassifierHypothesisLevel   = "Strong",
            PayloadJson                 = "{\"foo\":\"bar\"}",
        };

        var entity = TableDecisionTransitionRepository.ToEntity(original);
        var restored = TableDecisionTransitionRepository.FromEntity(entity);

        Assert.Equal(original.TenantId, restored.TenantId);
        Assert.Equal(original.SessionId, restored.SessionId);
        Assert.Equal(original.StepIndex, restored.StepIndex);
        Assert.Equal(original.SessionTraceOrdinal, restored.SessionTraceOrdinal);
        Assert.Equal(original.SignalOrdinalRef, restored.SignalOrdinalRef);
        Assert.Equal(original.OccurredAtUtc, restored.OccurredAtUtc.ToUniversalTime());
        Assert.Equal(original.Trigger, restored.Trigger);
        Assert.Equal(original.FromStage, restored.FromStage);
        Assert.Equal(original.ToStage, restored.ToStage);
        Assert.Equal(original.Taken, restored.Taken);
        Assert.Equal(original.DeadEndReason, restored.DeadEndReason);
        Assert.Equal(original.ReducerVersion, restored.ReducerVersion);
        Assert.Equal(original.IsTerminal, restored.IsTerminal);
        Assert.Equal(original.ClassifierVerdictId, restored.ClassifierVerdictId);
        Assert.Equal(original.ClassifierHypothesisLevel, restored.ClassifierHypothesisLevel);
        Assert.Equal(original.PayloadJson, restored.PayloadJson);
    }

    [Fact]
    public void FromEntity_reassembles_chunked_PayloadJson()
    {
        var oversized = new string('y', 75_000);
        var original = new DecisionTransitionRecord
        {
            TenantId            = TenantId,
            SessionId           = SessionId,
            StepIndex           = 1,
            SessionTraceOrdinal = 1,
            SignalOrdinalRef    = 1,
            OccurredAtUtc       = DateTime.UtcNow,
            Trigger             = "t",
            FromStage           = "F",
            ToStage             = "T",
            Taken               = true,
            ReducerVersion      = "1.0.0",
            PayloadJson         = oversized,
        };

        var entity = TableDecisionTransitionRepository.ToEntity(original);
        var restored = TableDecisionTransitionRepository.FromEntity(entity);

        Assert.Equal(75_000, restored.PayloadJson.Length);
        Assert.Equal(oversized, restored.PayloadJson);
    }
}
