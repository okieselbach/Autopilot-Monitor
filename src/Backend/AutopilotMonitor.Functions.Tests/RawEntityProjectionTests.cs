using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Functions.Helpers;
using Azure.Data.Tables;
using Xunit;

namespace AutopilotMonitor.Functions.Tests
{
    /// <summary>
    /// Unit coverage for <see cref="RawEntityProjection"/> — the shared serialiser behind the
    /// "raw" reader endpoints (/api/raw/sessions, /api/raw/events). These tests pin the two
    /// guarantees the finding hinged on:
    ///   1. EVERY stored column is preserved verbatim (no curated whitelist that silently drops
    ///      real columns like OsEdition / ImeAgentVersion / FailureSource).
    ///   2. fields= is a pure pass-through (narrows, never drops a real requested column) and always
    ///      retains PartitionKey + RowKey.
    /// </summary>
    public class RawEntityProjectionTests
    {
        private static TableEntity MakeSessionRow() => new TableEntity("tenant-1", "0834:session-1")
        {
            // A representative SessionsIndex row including columns the OLD whitelist dropped.
            { "SessionId", "session-1" },
            { "Status", "Succeeded" },
            { "Manufacturer", "Contoso" },
            { "Model", "X1" },
            { "EnrollmentType", "v2" },
            { "OsEdition", "Enterprise" },          // formerly dropped
            { "OsDisplayVersion", "24H2" },         // formerly dropped
            { "ImeAgentVersion", "1.23.456.789" },  // formerly dropped
            { "GeoCity", "Berlin" },                // formerly dropped
            { "FailureSource", "ESP" },             // formerly dropped
            { "DeletionState", "None" },            // formerly dropped
            { "EventCount", 42 },
            { "IsPreProvisioned", true },
        };

        [Fact]
        public void ToDictionary_PreservesEveryStoredColumn_IncludingFormerlyDropped()
        {
            var dict = RawEntityProjection.ToDictionary(MakeSessionRow());

            // System columns present.
            Assert.Equal("tenant-1", dict["PartitionKey"]);
            Assert.Equal("0834:session-1", dict["RowKey"]);
            Assert.True(dict.ContainsKey("Timestamp"));

            // Columns the old ProjectFields whitelist could never return are all here now.
            foreach (var col in new[] { "OsEdition", "OsDisplayVersion", "ImeAgentVersion", "GeoCity", "FailureSource", "DeletionState" })
                Assert.True(dict.ContainsKey(col), $"raw row must include stored column '{col}'");

            // Values are the literal stored values (no string coercion of ints/bools).
            Assert.Equal("Enterprise", dict["OsEdition"]);
            Assert.Equal(42, dict["EventCount"]);
            Assert.Equal(true, dict["IsPreProvisioned"]);
        }

        [Fact]
        public void ToDictionary_DropsOnlyTheAzureEtagBookkeepingKey()
        {
            var dict = RawEntityProjection.ToDictionary(MakeSessionRow());
            Assert.False(dict.ContainsKey("odata.etag"));
        }

        [Fact]
        public void Project_NullOrEmptyFields_ReturnsFullRawRow()
        {
            var rows = new List<IReadOnlyDictionary<string, object?>>
            {
                RawEntityProjection.ToDictionary(MakeSessionRow()),
            };

            foreach (var fields in new string?[] { null, "", "   " })
            {
                var projected = RawEntityProjection.Project(rows, fields);
                Assert.Single(projected);
                // Full shape retained — pick a few representative columns.
                Assert.True(projected[0].ContainsKey("OsEdition"));
                Assert.True(projected[0].ContainsKey("Status"));
                Assert.True(projected[0].ContainsKey("PartitionKey"));
            }
        }

        [Fact]
        public void Project_Fields_IsCaseInsensitivePassThrough_AndKeepsRealColumns()
        {
            var rows = new List<IReadOnlyDictionary<string, object?>>
            {
                RawEntityProjection.ToDictionary(MakeSessionRow()),
            };

            // Request a formerly-dropped column using lower-case to prove case-insensitivity.
            var projected = RawEntityProjection.Project(rows, "status,osedition,imeagentversion");
            var row = projected.Single();

            // Real requested columns are present (output keeps the stored PascalCase key name).
            Assert.True(row.ContainsKey("Status"));
            Assert.True(row.ContainsKey("OsEdition"));
            Assert.True(row.ContainsKey("ImeAgentVersion"));

            // Non-requested columns are narrowed away.
            Assert.False(row.ContainsKey("Manufacturer"));
            Assert.False(row.ContainsKey("Model"));
        }

        [Fact]
        public void Project_Fields_AlwaysRetainsPartitionKeyAndRowKey()
        {
            var rows = new List<IReadOnlyDictionary<string, object?>>
            {
                RawEntityProjection.ToDictionary(MakeSessionRow()),
            };

            // Caller asks only for Status — identity columns must still come back for cursor stability.
            var row = RawEntityProjection.Project(rows, "Status").Single();
            Assert.True(row.ContainsKey("PartitionKey"));
            Assert.True(row.ContainsKey("RowKey"));
            Assert.True(row.ContainsKey("Status"));
        }

        [Fact]
        public void Project_Fields_UnknownKeyIsSimplyAbsent_NeverThrows()
        {
            var rows = new List<IReadOnlyDictionary<string, object?>>
            {
                RawEntityProjection.ToDictionary(MakeSessionRow()),
            };

            var row = RawEntityProjection.Project(rows, "Status,NoSuchColumn").Single();
            Assert.True(row.ContainsKey("Status"));
            Assert.False(row.ContainsKey("NoSuchColumn"));
        }

        [Fact]
        public void ToDictionary_EventRow_KeepsDataJsonAsRawStringAndSeverityAsInt()
        {
            // A raw Events row: the raw tool must NOT parse DataJson or decode Severity.
            var entity = new TableEntity("tenant-1_session-1", "rowkey-1")
            {
                { "EventType", "app_install_failed" },
                { "Severity", 3 },                       // EventSeverity.Error, stored as int
                { "DataJson", "{\"win32\":\"0x80070002\"}" },
                { "Sequence", 17L },
            };

            var dict = RawEntityProjection.ToDictionary(entity);
            Assert.Equal(3, dict["Severity"]);                              // raw int, not "Error"
            Assert.Equal("{\"win32\":\"0x80070002\"}", dict["DataJson"]);   // raw string, not parsed
            Assert.Equal(17L, dict["Sequence"]);
        }
    }
}
