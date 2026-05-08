using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Locks in that <see cref="TenantNotificationService"/> emits the right SignalR push for each
/// lifecycle event — Create / Dismiss / DismissAll — and routes to the audience-correct group.
///
/// Why this matters: the web bell now relies entirely on SignalR for live updates (no polling).
/// If the service silently drops a push on a code path, the UI will show stale state until
/// the user reloads. That regression would be invisible without these tests.
/// </summary>
public class TenantNotificationServiceSignalRPushTests
{
    private const string TenantId = "test-tenant-aaaa-bbbb-cccc-dddddddddddd";

    private static (TenantNotificationService Service, FakeSignalRNotificationService Signalr, Mock<ITenantNotificationRepository> Repo) Build()
    {
        var repo = new Mock<ITenantNotificationRepository>();
        var signalr = new FakeSignalRNotificationService();
        var service = new TenantNotificationService(repo.Object, signalr, NullLogger<TenantNotificationService>.Instance);
        return (service, signalr, repo);
    }

    [Fact]
    public async Task Create_AdminTierType_PushesToAdminAudience()
    {
        var (svc, signalr, _) = Build();

        await svc.CreateNotificationAsync(TenantId, "hardware_rejection", "Title", "Message");

        var send = Assert.Single(signalr.TenantSends);
        Assert.Equal(TenantId, send.TenantId);
        Assert.Equal(NotificationAudience.Admin, send.Audience);
    }

    [Fact]
    public async Task Create_MemberTierType_PushesToMemberAudience()
    {
        var (svc, signalr, _) = Build();

        await svc.CreateNotificationAsync(TenantId, "sla_breach", "Title", "Message");

        var send = Assert.Single(signalr.TenantSends);
        Assert.Equal(NotificationAudience.Member, send.Audience);
    }

    [Fact]
    public async Task Create_UnknownType_DefaultsToAdminAudience_FailClosed()
    {
        var (svc, signalr, _) = Build();

        await svc.CreateNotificationAsync(TenantId, "brand_new_unregistered_type", "Title", "Message");

        // TenantNotificationAudienceCatalog.Resolve fails closed (Admin-only) for unknown types
        Assert.Equal(NotificationAudience.Admin, signalr.TenantSends.Single().Audience);
    }

    [Fact]
    public async Task Create_EmptyTenantId_NoPushNoRepoWrite()
    {
        var (svc, signalr, repo) = Build();

        await svc.CreateNotificationAsync("", "sla_breach", "Title", "Message");

        Assert.Empty(signalr.TenantSends);
        repo.Verify(r => r.AddNotificationAsync(It.IsAny<string>(), It.IsAny<GlobalNotification>()), Times.Never);
    }

    [Fact]
    public async Task Dismiss_RepoReturnsTrue_PushesDismissed()
    {
        var (svc, signalr, repo) = Build();
        repo.Setup(r => r.DismissNotificationAsync(TenantId, "n1", It.IsAny<string>())).ReturnsAsync(true);

        var ok = await svc.DismissNotificationAsync(TenantId, "n1", "user@example.com");

        Assert.True(ok);
        var dismiss = Assert.Single(signalr.TenantDismisses);
        Assert.Equal(TenantId, dismiss.TenantId);
        Assert.Equal("n1", dismiss.NotificationId);
    }

    [Fact]
    public async Task Dismiss_RepoReturnsFalse_NoPush()
    {
        var (svc, signalr, repo) = Build();
        repo.Setup(r => r.DismissNotificationAsync(TenantId, "missing", It.IsAny<string>())).ReturnsAsync(false);

        var ok = await svc.DismissNotificationAsync(TenantId, "missing", "user@example.com");

        Assert.False(ok);
        Assert.Empty(signalr.TenantDismisses);
    }

    [Fact]
    public async Task DismissAll_RepoReturnsCount_PushesOnceWithTenantId()
    {
        var (svc, signalr, repo) = Build();
        repo.Setup(r => r.DismissAllNotificationsAsync(TenantId)).ReturnsAsync(7);

        var count = await svc.DismissAllNotificationsAsync(TenantId);

        Assert.Equal(7, count);
        Assert.Equal(new[] { TenantId }, signalr.TenantDismissAlls);
    }

    [Fact]
    public async Task DismissAll_RepoReturnsZero_NoPush()
    {
        var (svc, signalr, repo) = Build();
        repo.Setup(r => r.DismissAllNotificationsAsync(TenantId)).ReturnsAsync(0);

        await svc.DismissAllNotificationsAsync(TenantId);

        Assert.Empty(signalr.TenantDismissAlls);
    }
}
