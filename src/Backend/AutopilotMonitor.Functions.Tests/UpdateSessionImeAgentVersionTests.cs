using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Codex-followup F1 (sub-finding): <c>UpdateSessionImeAgentVersionAsync</c> previously used
/// <c>UpsertEntityAsync</c>, which would silently recreate a partial Sessions row after the
/// cascade-delete worker had tombstoned it — breaking the manifest-snapshot invariant. The fix
/// switches to <c>UpdateEntityAsync(entity, ETag.All, Merge)</c>: 404 means "row already gone"
/// and is the correct no-op outcome.
/// </summary>
public class UpdateSessionImeAgentVersionTests
{
    private const string TenantId  = "11111111-1111-1111-1111-111111111111";
    private const string SessionId = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public async Task UpdateSessionImeAgentVersionAsync_uses_UpdateEntityAsync_not_UpsertEntityAsync()
    {
        var harness = new Harness();
        harness.Sessions
            .Setup(t => t.UpdateEntityAsync(It.IsAny<ITableEntity>(), It.IsAny<ETag>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<Response>());

        await harness.Sut.UpdateSessionImeAgentVersionAsync(TenantId, SessionId, "1.2.3.4");

        harness.Sessions.Verify(t => t.UpdateEntityAsync(
            It.Is<ITableEntity>(e => e.PartitionKey == TenantId && e.RowKey == SessionId),
            It.IsAny<ETag>(),
            TableUpdateMode.Merge,
            It.IsAny<CancellationToken>()),
            Times.Once);
        // The critical invariant: the old Upsert path is GONE — a tombstoned row must not be
        // resurrected by an in-flight ingest landing past the cascade lock.
        harness.Sessions.Verify(t => t.UpsertEntityAsync(
            It.IsAny<ITableEntity>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdateSessionImeAgentVersionAsync_swallows_404_when_row_is_tombstoned()
    {
        // The exact tombstone-revival scenario: cascade worker removed the Sessions row, an
        // in-flight ingest then tries to stamp ImeAgentVersion. UpdateEntityAsync surfaces 404 →
        // helper must return silently, NOT recreate the row.
        var harness = new Harness();
        harness.Sessions
            .Setup(t => t.UpdateEntityAsync(It.IsAny<ITableEntity>(), It.IsAny<ETag>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "Not Found"));

        await harness.Sut.UpdateSessionImeAgentVersionAsync(TenantId, SessionId, "1.2.3.4");

        // No Upsert anywhere — 404 is the silent success path.
        harness.Sessions.Verify(t => t.UpsertEntityAsync(
            It.IsAny<ITableEntity>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdateSessionImeAgentVersionAsync_swallows_other_storage_exceptions_as_warning()
    {
        // Method contract: failures are non-fatal. A 503 / 500 from storage logs a warning but
        // does not throw — preserves the pre-fix "don't block ingest" behaviour.
        var harness = new Harness();
        harness.Sessions
            .Setup(t => t.UpdateEntityAsync(It.IsAny<ITableEntity>(), It.IsAny<ETag>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(503, "ServiceUnavailable"));

        await harness.Sut.UpdateSessionImeAgentVersionAsync(TenantId, SessionId, "1.2.3.4");
        // No exception thrown.
    }

    private sealed class Harness
    {
        public Mock<TableClient> Sessions { get; }
        public TableStorageService Sut { get; }

        public Harness()
        {
            Sessions = new Mock<TableClient>();
            var mockServiceClient = new Mock<TableServiceClient>();
            mockServiceClient.Setup(s => s.GetTableClient(Constants.TableNames.Sessions)).Returns(Sessions.Object);
            Sut = new TableStorageService(mockServiceClient.Object, NullLogger<TableStorageService>.Instance);
        }
    }
}
