using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pure mapping tests for <see cref="TableIndexRepository"/>'s 5 <c>ToEntity</c> overloads
/// (Plan §2.8, §M5.d). Transaction-batching + Azurite round-trip are out of scope here;
/// this file verifies that every projected column and key lands on the entity as expected.
/// </summary>
public class TableIndexRepositoryMappingTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    private static readonly DateTime Occurred =
        new(2026, 4, 21, 14, 7, 33, DateTimeKind.Utc);

    // ============================================================ SessionsByTerminal

    [Fact]
    public void SessionsByTerminal_ToEntity_projects_keys_and_typed_columns()
    {
        var record = new SessionsByTerminalRecord
        {
            TenantId      = TenantId,
            SessionId     = SessionId,
            TerminalStage = "WhiteGloveSealed",
            OccurredAtUtc = Occurred,
            StepIndex     = 12,
        };

        var entity = TableIndexRepository.ToEntity(record);

        Assert.Equal($"{TenantId}_WhiteGloveSealed",   entity.PartitionKey);
        Assert.Equal($"20260421140733_{SessionId}",     entity.RowKey);
        Assert.Equal(TenantId,                          entity.GetString("TenantId"));
        Assert.Equal(SessionId,                         entity.GetString("SessionId"));
        Assert.Equal("WhiteGloveSealed",                entity.GetString("TerminalStage"));
        Assert.Equal(Occurred,                          entity.GetDateTime("OccurredAtUtc"));
        Assert.Equal(12,                                entity.GetInt32("StepIndex"));
    }

    // ============================================================ SessionsByStage

    [Fact]
    public void SessionsByStage_ToEntity_uses_padded_ticks_rowkey_for_lex_ordering()
    {
        var record = new SessionsByStageRecord
        {
            TenantId       = TenantId,
            SessionId      = SessionId,
            Stage          = "EspInProgress",
            LastUpdatedUtc = Occurred,
            StepIndex      = 4,
        };

        var entity = TableIndexRepository.ToEntity(record);

        Assert.Equal($"{TenantId}_EspInProgress",                   entity.PartitionKey);
        Assert.Equal($"{Occurred.Ticks:D19}_{SessionId}",           entity.RowKey);
        Assert.Equal("EspInProgress",                                entity.GetString("Stage"));
        Assert.Equal(Occurred,                                       entity.GetDateTime("LastUpdatedUtc"));
        Assert.Equal(4,                                              entity.GetInt32("StepIndex"));
    }

    // ============================================================ DeadEndsByReason

    [Fact]
    public void DeadEndsByReason_ToEntity_includes_attempted_stage_and_from_stage()
    {
        var record = new DeadEndsByReasonRecord
        {
            TenantId         = TenantId,
            SessionId        = SessionId,
            DeadEndReason    = "hybrid_reboot_gate_blocking",
            StepIndex        = 7,
            FromStage        = "EspInProgress",
            AttemptedToStage = "WhiteGloveSealed",
            OccurredAtUtc    = Occurred,
        };

        var entity = TableIndexRepository.ToEntity(record);

        Assert.Equal($"{TenantId}_hybrid_reboot_gate_blocking",   entity.PartitionKey);
        Assert.Equal($"20260421140733_{SessionId}_000007",          entity.RowKey);
        Assert.Equal("hybrid_reboot_gate_blocking",                 entity.GetString("DeadEndReason"));
        Assert.Equal(7,                                             entity.GetInt32("StepIndex"));
        Assert.Equal("EspInProgress",                               entity.GetString("FromStage"));
        Assert.Equal("WhiteGloveSealed",                            entity.GetString("AttemptedToStage"));
        Assert.Equal(Occurred,                                      entity.GetDateTime("OccurredAtUtc"));
    }

    // ============================================================ ClassifierVerdictsByIdLevel

    [Fact]
    public void ClassifierVerdictsByIdLevel_ToEntity_combines_classifierId_and_level_in_pk()
    {
        var record = new ClassifierVerdictsByIdLevelRecord
        {
            TenantId        = TenantId,
            SessionId       = SessionId,
            ClassifierId    = "whiteglove-sealing",
            HypothesisLevel = "Weak",
            StepIndex       = 5,
            OccurredAtUtc   = Occurred,
        };

        var entity = TableIndexRepository.ToEntity(record);

        Assert.Equal($"{TenantId}_whiteglove-sealing_Weak",      entity.PartitionKey);
        Assert.Equal($"20260421140733_{SessionId}_000005",        entity.RowKey);
        Assert.Equal("whiteglove-sealing",                        entity.GetString("ClassifierId"));
        Assert.Equal("Weak",                                      entity.GetString("HypothesisLevel"));
        Assert.Equal(5,                                           entity.GetInt32("StepIndex"));
        Assert.Equal(Occurred,                                    entity.GetDateTime("OccurredAtUtc"));
    }

    // ============================================================ SignalsByKind

    [Fact]
    public void SignalsByKind_ToEntity_carries_ordinal_backref_for_primary_row_hop()
    {
        var record = new SignalsByKindRecord
        {
            TenantId             = TenantId,
            SessionId            = SessionId,
            SignalKind           = "EspTerminalFailure",
            SessionSignalOrdinal = 987L,
            OccurredAtUtc        = Occurred,
            SourceOrigin         = "EspAndHelloTrackerAdapter",
        };

        var entity = TableIndexRepository.ToEntity(record);

        Assert.Equal($"{TenantId}_EspTerminalFailure",              entity.PartitionKey);
        Assert.Equal($"20260421140733_{SessionId}_0000000987",       entity.RowKey);
        Assert.Equal("EspTerminalFailure",                           entity.GetString("SignalKind"));
        Assert.Equal(987L,                                           entity.GetInt64("SessionSignalOrdinal"));
        Assert.Equal(Occurred,                                       entity.GetDateTime("OccurredAtUtc"));
        Assert.Equal("EspAndHelloTrackerAdapter",                    entity.GetString("SourceOrigin"));
    }

    // ============================================================ Null-tolerance on optional string columns

    [Fact]
    public void DeadEndsByReason_ToEntity_tolerates_null_strings_by_defaulting_to_empty()
    {
        // The repo is called from a queue worker; a malformed upstream message shouldn't NRE the mapper.
        var record = new DeadEndsByReasonRecord
        {
            TenantId         = TenantId,
            SessionId        = SessionId,
            DeadEndReason    = null!,
            StepIndex        = 1,
            FromStage        = null!,
            AttemptedToStage = null!,
            OccurredAtUtc    = Occurred,
        };

        var entity = TableIndexRepository.ToEntity(record);

        Assert.Equal(string.Empty, entity.GetString("DeadEndReason"));
        Assert.Equal(string.Empty, entity.GetString("FromStage"));
        Assert.Equal(string.Empty, entity.GetString("AttemptedToStage"));
    }
}
