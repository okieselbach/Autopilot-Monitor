using System.Collections.Generic;
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
/// SDK-mock tests for the CAS guard added to <see cref="TableStorageService.DeleteSessionAsync"/>
/// — Plan §5 PR5 finding 2. The legacy direct-delete path must refuse to tombstone a session
/// whose V2 cascade is in flight; without the guard, a scaled-out instance with stale tenant
/// config could race past the function-level pre-read and orphan every side-table row the
/// cascade was supposed to clean up.
/// </summary>
public class DeleteSessionCasGuardTests
{
    private const string TenantId  = "11111111-1111-1111-1111-111111111111";
    private const string SessionId = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public async Task DeleteSessionAsync_refuses_when_DeletionState_is_Queued()
    {
        // Sessions row is locked by an in-flight V2 cascade. The legacy path MUST NOT tombstone.
        var harness = new Harness();
        harness.SetSessionEntity(deletionState: SessionDeletionState.Queued);

        var result = await harness.Sut.DeleteSessionAsync(TenantId, SessionId);

        Assert.False(result);
        // Neither the Sessions DeleteEntity nor the SessionsIndex DeleteEntity may have run.
        harness.Sessions.Verify(t => t.DeleteEntityAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ETag>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.SessionsIndex.Verify(t => t.DeleteEntityAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ETag>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(SessionDeletionState.Preparing)]
    [InlineData(SessionDeletionState.Queued)]
    [InlineData(SessionDeletionState.Running)]
    [InlineData(SessionDeletionState.Poisoned)]
    public async Task DeleteSessionAsync_refuses_for_every_locked_state(string state)
    {
        // The lock guard fires for every non-None state in SessionDeletionState.IsLocked.
        var harness = new Harness();
        harness.SetSessionEntity(deletionState: state);

        var result = await harness.Sut.DeleteSessionAsync(TenantId, SessionId);

        Assert.False(result);
        harness.Sessions.Verify(t => t.DeleteEntityAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ETag>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DeleteSessionAsync_returns_false_when_etag_cas_loses_to_concurrent_producer()
    {
        // Read passes (DeletionState=None), but between our read and DeleteEntity a parallel V2
        // producer CAS-set DeletionState=Preparing → Azure returns 412. Legacy must abort.
        var harness = new Harness();
        harness.SetSessionEntity(deletionState: SessionDeletionState.None);
        harness.Sessions
            .Setup(t => t.DeleteEntityAsync(TenantId, SessionId, It.IsAny<ETag>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(412, "Precondition Failed"));

        var result = await harness.Sut.DeleteSessionAsync(TenantId, SessionId);

        Assert.False(result);
        // SessionsIndex must NOT be deleted when the Sessions tombstone CAS lost — the row is
        // still owned by the V2 cascade that just claimed it.
        harness.SessionsIndex.Verify(t => t.DeleteEntityAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ETag>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DeleteSessionAsync_passes_captured_etag_to_delete()
    {
        var harness = new Harness();
        harness.SetSessionEntity(deletionState: SessionDeletionState.None, etag: "0xMYETAG");
        harness.Sessions
            .Setup(t => t.DeleteEntityAsync(TenantId, SessionId, It.IsAny<ETag>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<Response>());
        harness.SessionsIndex
            .Setup(t => t.DeleteEntityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ETag>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<Response>());

        var result = await harness.Sut.DeleteSessionAsync(TenantId, SessionId);

        Assert.True(result);
        // Verify the ETag captured from the GetEntity read flows through to DeleteEntity — this
        // is what makes the CAS guard actually atomic at the storage layer.
        harness.Sessions.Verify(t => t.DeleteEntityAsync(
            TenantId, SessionId, It.Is<ETag>(e => e.ToString() == "0xMYETAG"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DeleteSessionAsync_returns_true_when_row_already_404()
    {
        // Idempotency: re-delete of an already-tombstoned session is a no-op success.
        var harness = new Harness();
        harness.Sessions
            .Setup(t => t.GetEntityAsync<TableEntity>(TenantId, SessionId, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "Not Found"));

        var result = await harness.Sut.DeleteSessionAsync(TenantId, SessionId);

        Assert.True(result);
    }

    [Fact]
    public async Task DeleteSessionAsync_treats_None_state_as_writable_happy_path()
    {
        // Empty / None / null DeletionState all mean "no cascade in flight" → legacy proceeds.
        var harness = new Harness();
        harness.SetSessionEntity(deletionState: SessionDeletionState.None);
        harness.Sessions
            .Setup(t => t.DeleteEntityAsync(TenantId, SessionId, It.IsAny<ETag>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<Response>());
        harness.SessionsIndex
            .Setup(t => t.DeleteEntityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ETag>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<Response>());

        Assert.True(await harness.Sut.DeleteSessionAsync(TenantId, SessionId));

        // Both deletes ran in the right order.
        harness.Sessions.Verify(t => t.DeleteEntityAsync(
            TenantId, SessionId, It.IsAny<ETag>(), It.IsAny<CancellationToken>()), Times.Once);
        harness.SessionsIndex.Verify(t => t.DeleteEntityAsync(
            TenantId, It.IsAny<string>(), It.IsAny<ETag>(), It.IsAny<CancellationToken>()), Times.AtMostOnce);
    }

    // ============================================================ Harness ====

    private sealed class Harness
    {
        public Mock<TableClient> Sessions { get; }
        public Mock<TableClient> SessionsIndex { get; }
        public TableStorageService Sut { get; }

        public Harness()
        {
            Sessions = new Mock<TableClient>();
            SessionsIndex = new Mock<TableClient>();
            var mockServiceClient = new Mock<TableServiceClient>();
            mockServiceClient.Setup(s => s.GetTableClient(Constants.TableNames.Sessions)).Returns(Sessions.Object);
            mockServiceClient.Setup(s => s.GetTableClient(Constants.TableNames.SessionsIndex)).Returns(SessionsIndex.Object);
            Sut = new TableStorageService(mockServiceClient.Object, NullLogger<TableStorageService>.Instance);
        }

        public void SetSessionEntity(string deletionState, string etag = "0xORIGINAL")
        {
            var entity = new TableEntity(TenantId, SessionId)
            {
                ["IndexRowKey"]   = "9999999999999999_22222222-2222-2222-2222-222222222222",
                ["DeletionState"] = deletionState,
            };
            entity.ETag = new ETag(etag);
            Sessions
                .Setup(t => t.GetEntityAsync<TableEntity>(TenantId, SessionId, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Response.FromValue(entity, Mock.Of<Response>()));
        }
    }
}
