using System;
using System.Collections.Generic;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins that the column-projected SessionsIndex scan driving <c>QuickSearchSessionsAsync</c>
/// (bcd307d — <c>select: QuickSearchProjection</c>) resolves the same match identity and builds the
/// same <see cref="QuickSearchResult"/> as the old full-row scan.
///
/// In real Azure Table Storage a <c>$select</c> returns ONLY the projected properties (plus the
/// structural PartitionKey/RowKey); every other column is genuinely absent. These tests reproduce
/// that faithfully — the "projected" row keeps only <see cref="TableStorageService.QuickSearchProjection"/>
/// — then run the production reads (<see cref="TableStorageService.ReadQuickSearchIdentity"/> +
/// <see cref="TableStorageService.BuildQuickSearchResult"/>) over both shapes and assert equivalence.
/// Deriving the keep-set from the production array means dropping a column there immediately fails
/// these tests (e.g. dropping "DeviceName" breaks the identity/result equivalence; dropping "RowKey"
/// breaks the SessionId fallback).
/// </summary>
public class QuickSearchProjectionEquivalenceTests
{
    private const string TenantId = "00000000-0000-0000-0000-000000000abc";

    private static readonly TableStorageService Sut =
        new(new Mock<TableServiceClient>().Object, NullLogger<TableStorageService>.Instance);

    // SessionsIndex RowKey = inverted-tick prefix + "_" + sessionId (see ComputeIndexRowKey).
    private static string IndexRowKey(DateTime startedAt, string sessionId)
        => $"{(DateTime.MaxValue.Ticks - startedAt.Ticks):D19}_{sessionId}";

    /// <summary>Full ~40-column mirror row as the unprojected scan would read it.</summary>
    private static TableEntity FullRow(string sessionId, string serial, string deviceName, string status, DateTime startedAt)
    {
        return new TableEntity(TenantId, IndexRowKey(startedAt, sessionId))
        {
            ["SessionId"] = sessionId,
            ["SerialNumber"] = serial,
            ["DeviceName"] = deviceName,
            ["Status"] = status,
            ["StartedAt"] = new DateTimeOffset(startedAt),
            // Representative noise the typeahead never reads — present on a real mirror row, absent
            // on the projected one. Proves the projection drops them without changing the outcome.
            ["Manufacturer"] = "Contoso",
            ["Model"] = "Model-X",
            ["OsName"] = "Windows 11",
            ["GeoCountry"] = "DE",
            ["EventCount"] = 123,
            ["FailureSnapshotJson"] = "{\"big\":\"" + new string('x', 2000) + "\"}",
        };
    }

    /// <summary>
    /// Reduces a full mirror row to exactly what Azure returns under
    /// <c>$select = QuickSearchProjection</c>: the projected columns plus the structural
    /// PartitionKey/RowKey; every other property is genuinely absent.
    /// </summary>
    private static TableEntity Project(TableEntity full)
    {
        var keep = new HashSet<string>(TableStorageService.QuickSearchProjection, StringComparer.Ordinal);
        var projected = new TableEntity(full.PartitionKey, full.RowKey);
        foreach (var kv in full)
        {
            if (kv.Key is "PartitionKey" or "RowKey" or "Timestamp" or "odata.etag") continue;
            if (keep.Contains(kv.Key)) projected[kv.Key] = kv.Value;
        }
        return projected;
    }

    [Fact]
    public void Identity_and_result_are_identical_for_full_vs_projected_row()
    {
        var started = DateTime.UtcNow.AddHours(-1);
        var sid = Guid.NewGuid().ToString();
        var full = FullRow(sid, "SN-12345", "PC-ALICE", "Succeeded", started);
        var projected = Project(full);

        // Match identity — the fields the typeahead matches on (SessionId, SerialNumber, DeviceName).
        var idFull = TableStorageService.ReadQuickSearchIdentity(full);
        var idProjected = TableStorageService.ReadQuickSearchIdentity(projected);
        Assert.Equal(idFull, idProjected);
        Assert.Equal(sid, idProjected.SessionId);
        Assert.Equal("SN-12345", idProjected.Serial);
        Assert.Equal("PC-ALICE", idProjected.DeviceName);

        // Result — the emitted DTO (Status + StartedAt read from the entity).
        var resFull = Sut.BuildQuickSearchResult(full, idFull.SessionId, idFull.Serial, idFull.DeviceName, "serialNumber");
        var resProjected = Sut.BuildQuickSearchResult(projected, idProjected.SessionId, idProjected.Serial, idProjected.DeviceName, "serialNumber");

        Assert.Equal(resFull.SessionId, resProjected.SessionId);
        Assert.Equal(resFull.SerialNumber, resProjected.SerialNumber);
        Assert.Equal(resFull.DeviceName, resProjected.DeviceName);
        Assert.Equal(resFull.Status, resProjected.Status);
        Assert.Equal(SessionStatus.Succeeded, resProjected.Status);
        Assert.Equal(resFull.StartedAt, resProjected.StartedAt);
        Assert.Equal(resFull.MatchedField, resProjected.MatchedField);
    }

    [Fact]
    public void SessionId_falls_back_to_the_RowKey_on_both_shapes_when_the_column_is_absent()
    {
        // A mirror row can lack the SessionId column; the scan then derives it from the RowKey.
        // RowKey is in QuickSearchProjection precisely so this fallback stays identical — this test
        // would fail if "RowKey" were ever dropped from the projection.
        var started = DateTime.UtcNow.AddHours(-2);
        var sid = Guid.NewGuid().ToString();
        var full = FullRow(sid, "SN-9", "PC-BOB", "InProgress", started);
        full.Remove("SessionId");

        var idFull = TableStorageService.ReadQuickSearchIdentity(full);
        var idProjected = TableStorageService.ReadQuickSearchIdentity(Project(full));

        Assert.Equal(sid, idFull.SessionId);        // resolved from RowKey
        Assert.Equal(idFull.SessionId, idProjected.SessionId);
    }

    [Fact]
    public void Missing_serial_and_device_collapse_to_empty_on_both_shapes()
    {
        var started = DateTime.UtcNow.AddHours(-3);
        var sid = Guid.NewGuid().ToString();
        var row = new TableEntity(TenantId, IndexRowKey(started, sid))
        {
            ["SessionId"] = sid,
            ["Status"] = "Failed",
            ["StartedAt"] = new DateTimeOffset(started),
        };

        var idFull = TableStorageService.ReadQuickSearchIdentity(row);
        var idProjected = TableStorageService.ReadQuickSearchIdentity(Project(row));

        Assert.Equal(idFull, idProjected);
        Assert.Equal(string.Empty, idProjected.Serial);
        Assert.Equal(string.Empty, idProjected.DeviceName);
    }
}
