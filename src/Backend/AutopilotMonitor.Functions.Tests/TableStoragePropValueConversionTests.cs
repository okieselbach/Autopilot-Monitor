using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models.Deletion;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Decode-direction tests for <see cref="TableStorageService.ConvertFromPropValue"/> +
/// <see cref="TableStorageService.ConvertDumpToEntity"/> (PR4b). Pairs with the existing
/// <c>DeletionManifestSerializationTests</c> which covers the encode direction. Together they
/// establish the byte-faithful EDM round-trip the manifest-as-backup guarantee depends on
/// (plan §13 / §3 "types preserved").
/// </summary>
public class TableStoragePropValueConversionTests
{
    [Fact]
    public void ConvertFromPropValue_round_trips_String()
    {
        var prop = MakeProp(DeletionPropEdmType.String, "\"hello world\"");
        Assert.Equal("hello world", TableStorageService.ConvertFromPropValue(prop));
    }

    [Fact]
    public void ConvertFromPropValue_round_trips_Boolean_true_and_false()
    {
        Assert.Equal(true, TableStorageService.ConvertFromPropValue(MakeProp(DeletionPropEdmType.Boolean, "true")));
        Assert.Equal(false, TableStorageService.ConvertFromPropValue(MakeProp(DeletionPropEdmType.Boolean, "false")));
    }

    [Fact]
    public void ConvertFromPropValue_round_trips_Int32()
    {
        Assert.Equal(42, TableStorageService.ConvertFromPropValue(MakeProp(DeletionPropEdmType.Int32, "42")));
        Assert.Equal(-1, TableStorageService.ConvertFromPropValue(MakeProp(DeletionPropEdmType.Int32, "-1")));
        Assert.Equal(int.MaxValue, TableStorageService.ConvertFromPropValue(MakeProp(DeletionPropEdmType.Int32, int.MaxValue.ToString(CultureInfo.InvariantCulture))));
    }

    [Fact]
    public void ConvertFromPropValue_round_trips_Int64()
    {
        Assert.Equal(123456789012L, TableStorageService.ConvertFromPropValue(MakeProp(DeletionPropEdmType.Int64, "123456789012")));
        Assert.Equal(long.MaxValue, TableStorageService.ConvertFromPropValue(MakeProp(DeletionPropEdmType.Int64, long.MaxValue.ToString(CultureInfo.InvariantCulture))));
    }

    [Fact]
    public void ConvertFromPropValue_round_trips_Double()
    {
        Assert.Equal(3.14, TableStorageService.ConvertFromPropValue(MakeProp(DeletionPropEdmType.Double, "3.14")));
        Assert.Equal(0.0, TableStorageService.ConvertFromPropValue(MakeProp(DeletionPropEdmType.Double, "0")));
    }

    [Fact]
    public void ConvertFromPropValue_round_trips_DateTime_as_UTC()
    {
        // Encode side stamps with "o" round-trip format and forces UTC kind.
        var json = JsonSerializer.Serialize(new DateTime(2026, 5, 12, 10, 0, 0, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture));
        var result = TableStorageService.ConvertFromPropValue(MakeProp(DeletionPropEdmType.DateTime, json));
        var dt = Assert.IsType<DateTime>(result);
        Assert.Equal(new DateTime(2026, 5, 12, 10, 0, 0, DateTimeKind.Utc), dt.ToUniversalTime());
        Assert.Equal(DateTimeKind.Utc, dt.Kind);
    }

    [Fact]
    public void ConvertFromPropValue_round_trips_Guid_in_D_format()
    {
        var guid = new Guid("33333333-3333-3333-3333-333333333333");
        var json = JsonSerializer.Serialize(guid.ToString("D"));
        var result = TableStorageService.ConvertFromPropValue(MakeProp(DeletionPropEdmType.Guid, json));
        Assert.Equal(guid, Assert.IsType<Guid>(result));
    }

    [Fact]
    public void ConvertFromPropValue_round_trips_Binary_as_Base64()
    {
        var bytes = new byte[] { 0x01, 0x02, 0x03, 0xff };
        var json = JsonSerializer.Serialize(Convert.ToBase64String(bytes));
        var result = TableStorageService.ConvertFromPropValue(MakeProp(DeletionPropEdmType.Binary, json));
        var decoded = Assert.IsType<byte[]>(result);
        Assert.Equal(bytes, decoded);
    }

    [Fact]
    public void ConvertFromPropValue_returns_null_for_JsonNull_value()
    {
        // Encode path stamps EdmType=String + JsonElement(null) for .NET null values.
        var prop = MakeProp(DeletionPropEdmType.String, "null");
        Assert.Null(TableStorageService.ConvertFromPropValue(prop));
    }

    [Fact]
    public void ConvertFromPropValue_throws_on_unknown_EdmType()
    {
        var prop = new DeletionPropValue
        {
            EdmType = "ThisEdmTypeDoesNotExist",
            Value = ParseJson("\"x\""),
        };
        Assert.Throws<System.IO.InvalidDataException>(() => TableStorageService.ConvertFromPropValue(prop));
    }

    [Fact]
    public void ConvertDumpToEntity_overrides_DeletionState_to_None_for_Sessions_row()
    {
        // Snapshot was captured after CAS-Preparing — without the override the restored row
        // would carry DeletionState=Preparing + the old manifestId, locking the session forever.
        var dump = new DeletionRowDump
        {
            Pk = "tenant-1",
            Rk = "session-1",
            Etag = "\"0xETAG\"",
            Props = new Dictionary<string, DeletionPropValue>(StringComparer.Ordinal)
            {
                ["DeletionState"] = MakeProp(DeletionPropEdmType.String, "\"Preparing\""),
                ["PendingDeletionManifestId"] = MakeProp(DeletionPropEdmType.String, "\"some-old-manifest\""),
                ["CurrentPhase"] = MakeProp(DeletionPropEdmType.String, "\"AccountSetup\""),
            },
        };

        var entity = TableStorageService.ConvertDumpToEntity(dump, Constants.TableNames.Sessions);

        Assert.Equal(SessionDeletionState.None, entity.GetString("DeletionState"));
        Assert.Null(entity["PendingDeletionManifestId"]);
        // Other props preserved.
        Assert.Equal("AccountSetup", entity.GetString("CurrentPhase"));
    }

    [Fact]
    public void ConvertDumpToEntity_does_NOT_override_DeletionState_for_other_tables()
    {
        // SessionsIndex (or any other table) shouldn't have DeletionState forced.
        var dump = new DeletionRowDump
        {
            Pk = "tenant-1",
            Rk = "rk-1",
            Props = new Dictionary<string, DeletionPropValue>(StringComparer.Ordinal)
            {
                ["SomeColumn"] = MakeProp(DeletionPropEdmType.String, "\"keep-me\""),
            },
        };

        var entity = TableStorageService.ConvertDumpToEntity(dump, Constants.TableNames.SessionsIndex);

        Assert.Equal("keep-me", entity.GetString("SomeColumn"));
        Assert.False(entity.ContainsKey("DeletionState")); // Not added by override
    }

    [Fact]
    public void ConvertDumpToEntity_preserves_pk_and_rk()
    {
        var dump = new DeletionRowDump
        {
            Pk = "tenant-foo",
            Rk = "session-bar",
            Props = new Dictionary<string, DeletionPropValue>(),
        };
        var entity = TableStorageService.ConvertDumpToEntity(dump, "AnyTable");
        Assert.Equal("tenant-foo", entity.PartitionKey);
        Assert.Equal("session-bar", entity.RowKey);
    }

    private static DeletionPropValue MakeProp(string edmType, string jsonValue) => new DeletionPropValue
    {
        EdmType = edmType,
        Value = ParseJson(jsonValue),
    };

    private static JsonElement ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
