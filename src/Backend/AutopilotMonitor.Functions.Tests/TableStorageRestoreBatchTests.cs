using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models.Deletion;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// SDK-mocked behaviour tests for <see cref="TableStorageService.RestoreRowsByExactKeysInBatchesAsync"/>
/// (PR4b). Mirror of <see cref="TableStorageDeletionBatchTests"/>: PK-grouped batches, 100-action
/// chunks, per-row fallback when batch surfaces 409. Verifies the Full vs Partial 409 semantics
/// asymmetry that distinguishes "row already there is a corruption signal" (Full) from "row
/// already there is expected" (Partial).
/// </summary>
public class TableStorageRestoreBatchTests
{
    private const string TableName = Constants.TableNames.Events;

    [Fact]
    public async Task Restore_returns_empty_result_for_empty_row_list()
    {
        var harness = new Harness();
        var result = await harness.Sut.RestoreRowsByExactKeysInBatchesAsync(TableName, new List<DeletionRowDump>(), RestoreMode.Full);

        Assert.Equal(0, result.Attempted);
        Assert.Equal(0, result.Restored);
        Assert.Equal(0, result.Skipped);
        Assert.Empty(harness.SubmittedBatches);
    }

    [Fact]
    public async Task Restore_groups_rows_by_partition_so_one_batch_per_pk()
    {
        var harness = new Harness();
        var rows = new[]
        {
            MakeDump("pkA", "rk1"), MakeDump("pkA", "rk2"),
            MakeDump("pkB", "rk3"), MakeDump("pkB", "rk4"), MakeDump("pkB", "rk5"),
            MakeDump("pkA", "rk6"),
        }.ToList();

        var result = await harness.Sut.RestoreRowsByExactKeysInBatchesAsync(TableName, rows, RestoreMode.Full);

        Assert.Equal(6, result.Attempted);
        Assert.Equal(6, result.Restored);
        Assert.Equal(0, result.Skipped);
        // Two batches: pkA (3 rows) + pkB (3 rows).
        Assert.Equal(2, harness.SubmittedBatches.Count);
        Assert.All(harness.SubmittedBatches, batch =>
        {
            var pks = batch.Select(a => a.Entity.PartitionKey).Distinct().ToList();
            Assert.Single(pks); // batch is partition-scoped
        });
    }

    [Fact]
    public async Task Restore_uses_Add_action_so_409_surfaces_for_existing_rows()
    {
        var harness = new Harness();
        await harness.Sut.RestoreRowsByExactKeysInBatchesAsync(
            TableName, new[] { MakeDump("pkA", "rk_0") }, RestoreMode.Full);

        var batch = harness.SubmittedBatches.Single();
        Assert.Equal(TableTransactionActionType.Add, batch.Single().ActionType);
    }

    [Fact]
    public async Task Restore_Full_mode_now_counts_409_as_skipped_like_Partial()
    {
        // PR4c F3b: Full mode used to throw InvalidDataException on per-row 409 (as a "corruption
        // signal"). That intent was hypothetical (Session GUID reuse is unlikely) and blocked the
        // operationally-real case of retrying a partial-failed restore. Both modes now share
        // 409-ignore semantics; RowsSkippedByTable surfaces the count for operator inspection.
        var harness = new Harness
        {
            BatchBehavior = _ => throw new RequestFailedException(409, "EntityAlreadyExists"),
            PerRowAddBehavior = _ => throw new RequestFailedException(409, "EntityAlreadyExists"),
        };

        var result = await harness.Sut.RestoreRowsByExactKeysInBatchesAsync(
            TableName, new[] { MakeDump("pkA", "rk_x") }, RestoreMode.Full);

        Assert.Equal(1, result.Attempted);
        Assert.Equal(0, result.Restored);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public async Task Restore_Partial_mode_counts_409_as_skipped_not_thrown()
    {
        var harness = new Harness
        {
            BatchBehavior = _ => throw new RequestFailedException(409, "EntityAlreadyExists"),
            PerRowAddBehavior = rk => rk == "rk_already_there"
                ? throw new RequestFailedException(409, "EntityAlreadyExists")
                : (Response)new Mock<Response>().Object,
        };

        var result = await harness.Sut.RestoreRowsByExactKeysInBatchesAsync(
            TableName,
            new[] { MakeDump("pkA", "rk_already_there"), MakeDump("pkA", "rk_new") },
            RestoreMode.Partial);

        Assert.Equal(2, result.Attempted);
        Assert.Equal(1, result.Restored);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public async Task Restore_propagates_non_409_non_400_storage_errors()
    {
        var harness = new Harness
        {
            BatchBehavior = _ => throw new RequestFailedException(503, "ServiceUnavailable"),
        };

        await Assert.ThrowsAsync<RequestFailedException>(() =>
            harness.Sut.RestoreRowsByExactKeysInBatchesAsync(
                TableName, new[] { MakeDump("pkA", "rk_0") }, RestoreMode.Full));
    }

    [Fact]
    public async Task Restore_perRow_treats_400_EntityAlreadyExists_as_Skipped()
    {
        // Codex-followup F4: Azurite and some legacy Azure deployments map duplicate-row conflict
        // to HTTP 400 (ErrorCode=EntityAlreadyExists) instead of 409. The batch catch already
        // tolerated 400, but the per-row fallback's `status == 409`-only filter rethrew the 400,
        // breaking idempotent restore-retry after partial-failed restore.
        var harness = new Harness
        {
            BatchBehavior = _ => throw new RequestFailedException(400, "EntityAlreadyExists", "EntityAlreadyExists", innerException: null),
            PerRowAddBehavior = _ => throw new RequestFailedException(400, "EntityAlreadyExists", "EntityAlreadyExists", innerException: null),
        };

        var result = await harness.Sut.RestoreRowsByExactKeysInBatchesAsync(
            TableName, new[] { MakeDump("pkA", "rk_dup") }, RestoreMode.Partial);

        Assert.Equal(1, result.Attempted);
        Assert.Equal(0, result.Restored);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public async Task Restore_perRow_400_without_EntityAlreadyExists_propagates()
    {
        // Negative case: a 400 that is NOT a duplicate-row conflict (e.g. validation error from
        // an oversized property) must still propagate. The new IsAlreadyExistsStatus helper keys
        // off ErrorCode = "EntityAlreadyExists" to distinguish.
        var harness = new Harness
        {
            BatchBehavior = _ => throw new RequestFailedException(400, "PropertyValueTooLarge", "PropertyValueTooLarge", innerException: null),
            PerRowAddBehavior = _ => throw new RequestFailedException(400, "PropertyValueTooLarge", "PropertyValueTooLarge", innerException: null),
        };

        var ex = await Assert.ThrowsAsync<RequestFailedException>(() =>
            harness.Sut.RestoreRowsByExactKeysInBatchesAsync(
                TableName, new[] { MakeDump("pkA", "rk_x") }, RestoreMode.Partial));

        Assert.Equal(400, ex.Status);
        Assert.Equal("PropertyValueTooLarge", ex.ErrorCode);
    }

    [Fact]
    public async Task Restore_throws_on_null_args()
    {
        var harness = new Harness();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.Sut.RestoreRowsByExactKeysInBatchesAsync("", new List<DeletionRowDump>(), RestoreMode.Full));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            harness.Sut.RestoreRowsByExactKeysInBatchesAsync(TableName, null!, RestoreMode.Full));
    }

    [Fact]
    public async Task Restore_preserves_dumps_props_via_ConvertDumpToEntity()
    {
        var harness = new Harness();
        var dump = new DeletionRowDump
        {
            Pk = "pkA",
            Rk = "rk_0",
            Props = new Dictionary<string, DeletionPropValue>(StringComparer.Ordinal)
            {
                ["MyStringCol"] = MakeProp(DeletionPropEdmType.String, "\"abc\""),
                ["MyIntCol"] = MakeProp(DeletionPropEdmType.Int32, "42"),
            },
        };

        await harness.Sut.RestoreRowsByExactKeysInBatchesAsync(TableName, new[] { dump }, RestoreMode.Full);

        var inserted = Assert.IsType<TableEntity>(harness.SubmittedBatches.Single().Single().Entity);
        Assert.Equal("abc", inserted.GetString("MyStringCol"));
        Assert.Equal(42, inserted.GetInt32("MyIntCol"));
    }

    // ---------------------------------------------------------------- Helpers ----

    private static DeletionRowDump MakeDump(string pk, string rk) => new DeletionRowDump
    {
        Pk = pk,
        Rk = rk,
        Props = new Dictionary<string, DeletionPropValue>(),
    };

    private static DeletionPropValue MakeProp(string edmType, string jsonValue)
    {
        using var doc = JsonDocument.Parse(jsonValue);
        return new DeletionPropValue { EdmType = edmType, Value = doc.RootElement.Clone() };
    }

    private sealed class Harness
    {
        public List<List<TableTransactionAction>> SubmittedBatches { get; } = new List<List<TableTransactionAction>>();
        public List<(string Pk, string Rk)> PerRowAdds { get; } = new List<(string, string)>();

        public Func<List<TableTransactionAction>, Response<IReadOnlyList<Response>>>? BatchBehavior { get; set; }
        public Func<string, Response>? PerRowAddBehavior { get; set; }

        public TableStorageService Sut { get; }

        public Harness()
        {
            var mockTableClient = new Mock<TableClient>();

            mockTableClient
                .Setup(c => c.SubmitTransactionAsync(It.IsAny<IEnumerable<TableTransactionAction>>(), It.IsAny<CancellationToken>()))
                .Returns<IEnumerable<TableTransactionAction>, CancellationToken>((actions, _) =>
                {
                    var snapshot = actions.ToList();
                    SubmittedBatches.Add(snapshot);
                    if (BatchBehavior != null) return Task.FromResult(BatchBehavior(snapshot));
                    var inner = (IReadOnlyList<Response>)snapshot.Select(_ => new Mock<Response>().Object).ToList();
                    return Task.FromResult(Response.FromValue(inner, new Mock<Response>().Object));
                });

            mockTableClient
                .Setup(c => c.AddEntityAsync(It.IsAny<ITableEntity>(), It.IsAny<CancellationToken>()))
                .Returns<ITableEntity, CancellationToken>((entity, _) =>
                {
                    PerRowAdds.Add((entity.PartitionKey, entity.RowKey));
                    if (PerRowAddBehavior != null) return Task.FromResult(PerRowAddBehavior(entity.RowKey));
                    return Task.FromResult(new Mock<Response>().Object);
                });

            var mockServiceClient = new Mock<TableServiceClient>();
            mockServiceClient.Setup(s => s.GetTableClient(It.IsAny<string>())).Returns(mockTableClient.Object);

            Sut = new TableStorageService(mockServiceClient.Object, NullLogger<TableStorageService>.Instance);
        }
    }
}
