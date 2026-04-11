using Azure.Data.Tables;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using Microsoft.Extensions.Logging;

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

    [Fact]
    public void ChunkProperty_SurrogatePairOnBoundary_DoesNotSplitPair()
    {
        // Place an emoji (UTF-16 surrogate pair) such that the high surrogate lands
        // at index 29_999 and the low surrogate at index 30_000 — the naive split point.
        var prefix = new string('a', 29_999);
        var emoji = "\uD83D\uDE00"; // U+1F600 GRINNING FACE
        var suffix = new string('b', 5);
        var value = prefix + emoji + suffix; // length 30_006

        var result = TableStorageChunking.ChunkProperty("Prop", value);

        Assert.Equal("2", result["Prop_ChunkCount"]);
        var chunk0 = result["Prop_0"];
        var chunk1 = result["Prop_1"];

        // Neither chunk may contain an orphan surrogate.
        Assert.False(char.IsHighSurrogate(chunk0[^1]), "Chunk 0 ends with orphan high surrogate");
        Assert.False(char.IsLowSurrogate(chunk1[0]), "Chunk 1 starts with orphan low surrogate");

        // Round-trip preserves data.
        Assert.Equal(value, chunk0 + chunk1);

        // And the high surrogate was moved into chunk 1 (not chunk 0).
        Assert.Equal(29_999, chunk0.Length);
        Assert.Equal('\uD83D', chunk1[0]);
        Assert.Equal('\uDE00', chunk1[1]);
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

    // ===== Corruption detection =====

    [Fact]
    public void ReassembleDict_MissingMiddleChunk_ReturnsPartialAndLogsWarning()
    {
        var entity = new Dictionary<string, object>
        {
            { "Prop_0", "aaa" },
            { "Prop_2", "ccc" },
            { "Prop_ChunkCount", "3" }
        };
        var logger = new CapturingLogger();

        var result = TableStorageChunking.ReassembleProperty(entity, "Prop", logger, "pk/rk");

        Assert.Equal("aaaccc", result);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("expected 3", warning.Message);
        Assert.Contains("got 2", warning.Message);
        Assert.Contains("pk/rk", warning.Message);
    }

    [Fact]
    public void ReassembleDict_ChunkCountWithoutChunks_ReturnsNullAndLogsWarning()
    {
        var entity = new Dictionary<string, object>
        {
            { "Prop_ChunkCount", "3" }
        };
        var logger = new CapturingLogger();

        var result = TableStorageChunking.ReassembleProperty(entity, "Prop", logger, "pk/rk");

        Assert.Null(result);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("no chunks found", warning.Message);
    }

    [Fact]
    public void ReassembleDict_InvalidChunkCount_ReturnsNullAndLogsWarning()
    {
        var entity = new Dictionary<string, object>
        {
            { "Prop_0", "aaa" },
            { "Prop_ChunkCount", "abc" }
        };
        var logger = new CapturingLogger();

        var result = TableStorageChunking.ReassembleProperty(entity, "Prop", logger, "pk/rk");

        Assert.Null(result);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("invalid ChunkCount", warning.Message);
        Assert.Contains("abc", warning.Message);
    }

    [Fact]
    public void ReassembleTableEntity_MissingMiddleChunk_ReturnsPartialAndLogsWarning()
    {
        var entity = new TableEntity("pk", "rk")
        {
            ["Prop_0"] = "aaa",
            ["Prop_2"] = "ccc",
            ["Prop_ChunkCount"] = "3"
        };
        var logger = new CapturingLogger();

        var result = TableStorageChunking.ReassembleProperty(entity, "Prop", logger, "pk/rk");

        Assert.Equal("aaaccc", result);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("expected 3", warning.Message);
        Assert.Contains("got 2", warning.Message);
    }

    [Fact]
    public void ReassembleDict_HappyPath_DoesNotLog()
    {
        var entity = new Dictionary<string, object>
        {
            { "Prop_0", "aaa" },
            { "Prop_1", "bbb" },
            { "Prop_ChunkCount", "2" }
        };
        var logger = new CapturingLogger();

        var result = TableStorageChunking.ReassembleProperty(entity, "Prop", logger, "pk/rk");

        Assert.Equal("aaabbb", result);
        Assert.Empty(logger.Entries);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
