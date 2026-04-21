using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pure mapping tests for <see cref="TableSignalRepository"/>. The transaction pipeline itself
/// needs a live Azurite harness (out of scope for unit tests); the deterministic bit — how the
/// record projects onto the Azure Table entity — is covered here.
/// </summary>
public class TableSignalRepositoryMappingTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    [Fact]
    public void BuildPartitionKey_is_tenant_underscore_session()
    {
        var pk = TableSignalRepository.BuildPartitionKey(TenantId, SessionId);
        Assert.Equal($"{TenantId}_{SessionId}", pk);
    }

    [Fact]
    public void BuildRowKey_pads_ordinal_to_19_digits_for_lex_ordering()
    {
        Assert.Equal("0000000000000000000", TableSignalRepository.BuildRowKey(0));
        Assert.Equal("0000000000000000042", TableSignalRepository.BuildRowKey(42));
        Assert.Equal("9223372036854775807", TableSignalRepository.BuildRowKey(long.MaxValue));
    }

    [Fact]
    public void BuildRowKey_orders_lexicographically_matching_numeric_order()
    {
        var a = TableSignalRepository.BuildRowKey(9);
        var b = TableSignalRepository.BuildRowKey(10);
        // 19-digit padding means string comparison matches numeric order (the property of the key).
        Assert.True(string.CompareOrdinal(a, b) < 0);
    }

    [Fact]
    public void ToEntity_projects_all_typed_columns()
    {
        var record = new SignalRecord
        {
            TenantId             = TenantId,
            SessionId            = SessionId,
            SessionSignalOrdinal = 17,
            SessionTraceOrdinal  = 117,
            Kind                 = "EspPhaseChanged",
            KindSchemaVersion    = 2,
            OccurredAtUtc        = new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc),
            SourceOrigin         = "EspAndHelloTrackerAdapter",
            PayloadJson          = "{\"foo\":\"bar\"}",
        };

        var entity = TableSignalRepository.ToEntity(record);

        Assert.Equal($"{TenantId}_{SessionId}", entity.PartitionKey);
        Assert.Equal("0000000000000000017", entity.RowKey);
        Assert.Equal(TenantId, entity.GetString("TenantId"));
        Assert.Equal(SessionId, entity.GetString("SessionId"));
        Assert.Equal(17L, entity.GetInt64("SessionSignalOrdinal"));
        Assert.Equal(117L, entity.GetInt64("SessionTraceOrdinal"));
        Assert.Equal("EspPhaseChanged", entity.GetString("Kind"));
        Assert.Equal(2, entity.GetInt32("KindSchemaVersion"));
        Assert.Equal(record.OccurredAtUtc, entity.GetDateTime("OccurredAtUtc"));
        Assert.Equal("EspAndHelloTrackerAdapter", entity.GetString("SourceOrigin"));
        Assert.Equal("{\"foo\":\"bar\"}", entity.GetString("PayloadJson"));
    }

    [Fact]
    public void ToEntity_chunks_oversized_PayloadJson_to_survive_the_32K_char_per_property_limit()
    {
        var oversized = new string('x', 75_000);
        var record = new SignalRecord
        {
            TenantId             = TenantId,
            SessionId            = SessionId,
            SessionSignalOrdinal = 1,
            SessionTraceOrdinal  = 1,
            Kind                 = "EspPhaseChanged",
            KindSchemaVersion    = 1,
            OccurredAtUtc        = DateTime.UtcNow,
            SourceOrigin         = "test",
            PayloadJson          = oversized,
        };

        var entity = TableSignalRepository.ToEntity(record);

        Assert.Equal("3", entity.GetString("PayloadJson_ChunkCount"));
        Assert.Equal(30_000, entity.GetString("PayloadJson_0").Length);
        Assert.Equal(30_000, entity.GetString("PayloadJson_1").Length);
        Assert.Equal(15_000, entity.GetString("PayloadJson_2").Length);
        Assert.False(entity.ContainsKey("PayloadJson"));
    }

    [Fact]
    public void ToEntity_tolerates_null_PayloadJson_without_throwing()
    {
        var record = new SignalRecord
        {
            TenantId             = TenantId,
            SessionId            = SessionId,
            SessionSignalOrdinal = 1,
            SessionTraceOrdinal  = 1,
            Kind                 = "EspPhaseChanged",
            KindSchemaVersion    = 1,
            OccurredAtUtc        = DateTime.UtcNow,
            SourceOrigin         = "test",
            PayloadJson          = null!,
        };

        var entity = TableSignalRepository.ToEntity(record);

        Assert.Equal(string.Empty, entity.GetString("PayloadJson"));
    }
}
