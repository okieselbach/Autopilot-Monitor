using System.Threading;
using System.Threading.Tasks;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Services.Deletion;
using AutopilotMonitor.Shared.Models.Deletion;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Behaviour tests for <see cref="SessionDeletionGuard"/>. The guard is the single chokepoint
/// every wired writer goes through (PR5 wiring per §5 PR3 table). PR3 ships the guard +
/// exception type only; this test class verifies the throw / no-throw + outcome translation
/// shape so PR5 sites can confidently rely on the contract.
/// </summary>
public class SessionDeletionGuardTests
{
    private const string TenantId  = "11111111-1111-1111-1111-111111111111";
    private const string SessionId = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public void ThrowIfLocked_returns_silently_on_null_session_row()
    {
        var guard = NewGuard(out _);
        // No-op when the row is null — caller treats absence per its own contract.
        guard.ThrowIfLocked((TableEntity?)null, callerContext: "Test");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("None")]
    public void ThrowIfLocked_returns_silently_when_state_is_None_or_legacy(string? state)
    {
        var guard = NewGuard(out _);
        var row = new TableEntity(TenantId, SessionId);
        if (state != null) row["DeletionState"] = state;
        // Legacy rows pre-PR3 have no DeletionState column → null lookup → treated as not locked.
        guard.ThrowIfLocked(row, callerContext: "Test");
    }

    [Theory]
    [InlineData(SessionDeletionState.Preparing)]
    [InlineData(SessionDeletionState.Queued)]
    [InlineData(SessionDeletionState.Running)]
    [InlineData(SessionDeletionState.Poisoned)]
    public void ThrowIfLocked_throws_for_every_lock_state(string state)
    {
        var guard = NewGuard(out _);
        var row = new TableEntity(TenantId, SessionId)
        {
            ["DeletionState"] = state,
            ["PendingDeletionManifestId"] = "DEAD-BEEF-MANIFEST",
        };

        var ex = Assert.Throws<SessionDeletionLockedException>(
            () => guard.ThrowIfLocked(row, callerContext: "TelemetryIngest"));

        Assert.Equal(TenantId, ex.TenantId);
        Assert.Equal(SessionId, ex.SessionId);
        Assert.Equal("TelemetryIngest", ex.CallerContext);
        Assert.Equal(state, ex.CurrentState);
        Assert.Equal("DEAD-BEEF-MANIFEST", ex.ManifestId);
        Assert.Contains(state, ex.Message);
        Assert.Contains("DEAD-BEEF-MANIFEST", ex.Message);
    }

    [Fact]
    public async Task EnsureWritableAsync_returns_silently_when_session_row_is_missing()
    {
        // Session not registered yet OR cascade tombstone has already removed it — caller must
        // handle session-not-found per its own rules (the guard simply doesn't block).
        var guard = NewGuard(out var reader);
        reader.Setup(r => r.GetSessionRowAsync(TenantId, SessionId, It.IsAny<CancellationToken>()))
              .ReturnsAsync((TableEntity?)null);

        await guard.EnsureWritableAsync(TenantId, SessionId, callerContext: "VulnerabilityCorrelate");
    }

    [Fact]
    public async Task EnsureWritableAsync_returns_silently_when_state_is_None()
    {
        var guard = NewGuard(out var reader);
        var row = new TableEntity(TenantId, SessionId)
        {
            ["DeletionState"] = SessionDeletionState.None,
        };
        reader.Setup(r => r.GetSessionRowAsync(TenantId, SessionId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(row);

        await guard.EnsureWritableAsync(TenantId, SessionId, callerContext: "AnalyzeOnEnrollmentEnd");
    }

    [Fact]
    public async Task EnsureWritableAsync_throws_when_state_is_locked()
    {
        var guard = NewGuard(out var reader);
        var row = new TableEntity(TenantId, SessionId)
        {
            ["DeletionState"] = SessionDeletionState.Running,
            ["PendingDeletionManifestId"] = "MANIFEST-ABC",
        };
        reader.Setup(r => r.GetSessionRowAsync(TenantId, SessionId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(row);

        var ex = await Assert.ThrowsAsync<SessionDeletionLockedException>(
            () => guard.EnsureWritableAsync(TenantId, SessionId, callerContext: "IndexReconcile"));

        Assert.Equal(SessionDeletionState.Running, ex.CurrentState);
        Assert.Equal("IndexReconcile", ex.CallerContext);
        Assert.Equal("MANIFEST-ABC", ex.ManifestId);
    }

    [Fact]
    public async Task EnsureWritableAsync_uses_in_hand_PartitionKey_RowKey_in_exception()
    {
        // Standalone read returns whatever the table has — including the canonical (PK, RK).
        // The exception carries those exact values so audit / log lines pin to the right row.
        var guard = NewGuard(out var reader);
        var row = new TableEntity(TenantId, SessionId)
        {
            ["DeletionState"] = SessionDeletionState.Poisoned,
        };
        reader.Setup(r => r.GetSessionRowAsync(TenantId, SessionId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(row);

        var ex = await Assert.ThrowsAsync<SessionDeletionLockedException>(
            () => guard.EnsureWritableAsync(TenantId, SessionId, callerContext: "ManualRescan"));

        Assert.Equal(TenantId, ex.TenantId);
        Assert.Equal(SessionId, ex.SessionId);
    }

    private static SessionDeletionGuard NewGuard(out Mock<ISessionDeletionInventoryReader> reader)
    {
        reader = new Mock<ISessionDeletionInventoryReader>();
        return new SessionDeletionGuard(reader.Object, NullLogger<SessionDeletionGuard>.Instance);
    }
}
