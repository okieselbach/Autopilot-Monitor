using Azure.Data.Tables;
using AutopilotMonitor.Functions.DataAccess.TableStorage;

namespace AutopilotMonitor.Functions.Tests;

public class TableStorageChunkingTests
{
    // ===== ChunkProperty =====

    [Fact]
    public void ChunkProperty_SmallValue_ReturnsSingleProperty()
    {
        var result = TableStorageChunking.ChunkProperty("Prop", "hello");

        Assert.Single(result);
        Assert.Equal("hello", result["Prop"]);
    }

    [Fact]
    public void ChunkProperty_ExactlyAtLimit_ReturnsSingleProperty()
    {
        var value = new string('x', 30_000);
        var result = TableStorageChunking.ChunkProperty("Prop", value);

        Assert.Single(result);
        Assert.Equal(value, result["Prop"]);
    }

    [Fact]
    public void ChunkProperty_OneCharOverLimit_ReturnsTwoChunks()
    {
        var value = new string('x', 30_001);
        var result = TableStorageChunking.ChunkProperty("Prop", value);

        Assert.Equal("2", result["Prop_ChunkCount"]);
        Assert.Equal(30_000, result["Prop_0"].Length);
        Assert.Equal(1, result["Prop_1"].Length);
        Assert.Equal(value, result["Prop_0"] + result["Prop_1"]);
    }

    [Fact]
    public void ChunkProperty_LargeValue_ChunksCorrectly()
    {
        var value = new string('a', 75_000);
        var result = TableStorageChunking.ChunkProperty("CveDataJson", value);

        Assert.Equal("3", result["CveDataJson_ChunkCount"]);
        Assert.Equal(30_000, result["CveDataJson_0"].Length);
        Assert.Equal(30_000, result["CveDataJson_1"].Length);
        Assert.Equal(15_000, result["CveDataJson_2"].Length);
        Assert.Equal(value, result["CveDataJson_0"] + result["CveDataJson_1"] + result["CveDataJson_2"]);
    }

    [Fact]
    public void ChunkProperty_EmptyString_ReturnsSingleProperty()
    {
        var result = TableStorageChunking.ChunkProperty("Prop", "");

        Assert.Single(result);
        Assert.Equal("", result["Prop"]);
    }

    [Fact]
    public void ChunkProperty_NullValue_ReturnsSingleEmptyProperty()
    {
        var result = TableStorageChunking.ChunkProperty("Prop", null!);

        Assert.Single(result);
        Assert.Equal("", result["Prop"]);
    }

    // ===== ReassembleProperty (IDictionary) =====

    [Fact]
    public void ReassembleDict_SingleProperty_ReturnsValue()
    {
        var entity = new Dictionary<string, object> { { "Prop", "hello" } };

        Assert.Equal("hello", TableStorageChunking.ReassembleProperty(entity, "Prop"));
    }

    [Fact]
    public void ReassembleDict_ChunkedProperty_ReassemblesCorrectly()
    {
        var entity = new Dictionary<string, object>
        {
            { "Prop_0", "aaa" },
            { "Prop_1", "bbb" },
            { "Prop_ChunkCount", "2" }
        };

        Assert.Equal("aaabbb", TableStorageChunking.ReassembleProperty(entity, "Prop"));
    }

    [Fact]
    public void ReassembleDict_MissingProperty_ReturnsNull()
    {
        var entity = new Dictionary<string, object> { { "Other", "value" } };

        Assert.Null(TableStorageChunking.ReassembleProperty(entity, "Prop"));
    }

    [Fact]
    public void ReassembleDict_RoundTrip_PreservesData()
    {
        var original = new string('z', 90_000);
        var chunks = TableStorageChunking.ChunkProperty("Data", original);

        var entity = new Dictionary<string, object>();
        foreach (var kv in chunks)
            entity[kv.Key] = kv.Value;

        var reassembled = TableStorageChunking.ReassembleProperty(entity, "Data");
        Assert.Equal(original, reassembled);
    }

    // ===== ReassembleProperty (TableEntity) =====

    [Fact]
    public void ReassembleTableEntity_SingleProperty_ReturnsValue()
    {
        var entity = new TableEntity("pk", "rk") { ["Prop"] = "hello" };

        Assert.Equal("hello", TableStorageChunking.ReassembleProperty(entity, "Prop"));
    }

    [Fact]
    public void ReassembleTableEntity_ChunkedProperty_ReassemblesCorrectly()
    {
        var entity = new TableEntity("pk", "rk")
        {
            ["Prop_0"] = "aaa",
            ["Prop_1"] = "bbb",
            ["Prop_ChunkCount"] = "2"
        };

        Assert.Equal("aaabbb", TableStorageChunking.ReassembleProperty(entity, "Prop"));
    }

    [Fact]
    public void ReassembleTableEntity_MissingProperty_ReturnsNull()
    {
        var entity = new TableEntity("pk", "rk") { ["Other"] = "value" };

        Assert.Null(TableStorageChunking.ReassembleProperty(entity, "Prop"));
    }

    [Fact]
    public void ReassembleTableEntity_RoundTrip_PreservesData()
    {
        var original = new string('z', 90_000);
        var chunks = TableStorageChunking.ChunkProperty("Data", original);

        var entity = new TableEntity("pk", "rk");
        foreach (var kv in chunks)
            entity[kv.Key] = kv.Value;

        var reassembled = TableStorageChunking.ReassembleProperty(entity, "Data");
        Assert.Equal(original, reassembled);
    }
}
