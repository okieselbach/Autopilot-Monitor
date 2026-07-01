using System;
using System.Collections.Generic;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins that the column-projected SessionsIndex scan driving <c>GetMetricsSummaryAsync</c>
/// (bcd307d — <c>select: MetricsSummaryProjection</c> = { PartitionKey, Status }) tallies the
/// same per-tenant status buckets as the old full-row drain.
///
/// In real Azure Table Storage a <c>$select</c> returns ONLY the projected properties (plus the
/// structural PartitionKey/RowKey); every other column is genuinely absent. These tests reproduce
/// that faithfully — the "projected" row keeps only <see cref="TableStorageService.MetricsSummaryProjection"/>
/// — then run the production tally (<see cref="TableStorageService.TallyMetricsSummaryRow"/>) over
/// both shapes and assert identical buckets. Deriving the keep-set from the production array means
/// dropping a column there (e.g. Status) immediately fails these tests.
/// </summary>
public class MetricsSummaryProjectionEquivalenceTests
{
    private const string TenantA = "00000000-0000-0000-0000-00000000aaaa";
    private const string TenantB = "00000000-0000-0000-0000-00000000bbbb";

    private static string IndexRowKey(DateTime startedAt, string sessionId)
        => $"{(DateTime.MaxValue.Ticks - startedAt.Ticks):D19}_{sessionId}";

    /// <summary>Full ~40-column mirror row as the cross-partition scan would read it (no projection).</summary>
    private static TableEntity FullRow(string tenantId, string status, DateTime startedAt)
    {
        var sid = Guid.NewGuid().ToString();
        return new TableEntity(tenantId, IndexRowKey(startedAt, sid))
        {
            ["SessionId"] = sid,
            ["Status"] = status,
            ["StartedAt"] = new DateTimeOffset(startedAt),
            // Representative noise the tally never reads — present on a real mirror row, absent on
            // the projected one. Proves the projection drops them without changing the outcome.
            ["SerialNumber"] = "SN-FULL",
            ["DeviceName"] = "PC-FULL",
            ["OsName"] = "Windows 11",
            ["GeoCountry"] = "DE",
            ["EventCount"] = 123,
            ["FailureSnapshotJson"] = "{\"big\":\"" + new string('x', 2000) + "\"}",
        };
    }

    /// <summary>
    /// Reduces a full mirror row to exactly what Azure returns under
    /// <c>$select = MetricsSummaryProjection</c>: the projected columns plus the structural
    /// PartitionKey/RowKey; every other property is genuinely absent.
    /// </summary>
    private static TableEntity Project(TableEntity full)
    {
        var keep = new HashSet<string>(TableStorageService.MetricsSummaryProjection, StringComparer.Ordinal);
        var projected = new TableEntity(full.PartitionKey, full.RowKey);
        foreach (var kv in full)
        {
            if (kv.Key is "PartitionKey" or "RowKey" or "Timestamp" or "odata.etag") continue;
            if (keep.Contains(kv.Key)) projected[kv.Key] = kv.Value;
        }
        return projected;
    }

    private static Dictionary<string, SessionStatusBuckets> Tally(IEnumerable<TableEntity> rows)
    {
        var groups = new Dictionary<string, SessionStatusBuckets>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows) TableStorageService.TallyMetricsSummaryRow(groups, r);
        return groups;
    }

    private static void AssertBucketsEqual(SessionStatusBuckets a, SessionStatusBuckets b)
    {
        Assert.Equal(a.Total, b.Total);
        Assert.Equal(a.Succeeded, b.Succeeded);
        Assert.Equal(a.Failed, b.Failed);
        Assert.Equal(a.InProgress, b.InProgress);
        Assert.Equal(a.Pending, b.Pending);
        Assert.Equal(a.Stalled, b.Stalled);
        Assert.Equal(a.Other, b.Other);
    }

    [Fact]
    public void Tally_is_identical_for_full_vs_projected_fleet()
    {
        var now = DateTime.UtcNow;
        // A mixed multi-tenant fleet covering every bucket the summary reports.
        var fleet = new (string Tenant, string Status)[]
        {
            (TenantA, "Succeeded"),
            (TenantA, "Succeeded"),
            (TenantA, "Failed"),
            (TenantA, "InProgress"),
            (TenantA, "Pending"),
            (TenantA, "Stalled"),
            (TenantA, "SomethingUnexpected"), // -> Other bucket
            (TenantB, "Succeeded"),
            (TenantB, "Failed"),
        };

        var fullRows = new List<TableEntity>();
        var projectedRows = new List<TableEntity>();
        var i = 0;
        foreach (var (tenant, status) in fleet)
        {
            var full = FullRow(tenant, status, now.AddMinutes(-i++));
            fullRows.Add(full);
            projectedRows.Add(Project(full));
        }

        var fullGroups = Tally(fullRows);
        var projectedGroups = Tally(projectedRows);

        Assert.Equal(fullGroups.Count, projectedGroups.Count);
        foreach (var tenant in fullGroups.Keys)
        {
            Assert.True(projectedGroups.ContainsKey(tenant));
            AssertBucketsEqual(fullGroups[tenant], projectedGroups[tenant]);
        }
    }

    [Fact]
    public void Missing_status_column_defaults_to_inprogress_on_both_shapes()
    {
        // A mirror row can lack Status (defaults to "InProgress"). The projection keeps Status, so
        // full and projected must agree — including on the missing-column default.
        var row = new TableEntity(TenantA, IndexRowKey(DateTime.UtcNow, Guid.NewGuid().ToString()))
        {
            ["SerialNumber"] = "SN-NOSTATUS",
        };

        var full = Tally(new[] { row });
        var projected = Tally(new[] { Project(row) });

        AssertBucketsEqual(full[TenantA], projected[TenantA]);
        Assert.Equal(1, full[TenantA].InProgress);
    }
}
