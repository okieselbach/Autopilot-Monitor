using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for <see cref="TenantNotificationService.GetActiveNotificationsAsync"/> audience filtering.
///
/// CORRECTNESS GUARD: Member-tier callers (Operator/Viewer) must NOT see Admin-tier notifications
/// (e.g. <c>hardware_rejection</c>) — those expose admin-only links and information that non-admins
/// cannot act on. The catalog at <see cref="TenantNotificationAudienceCatalog"/> drives this; this
/// test locks in that the service actually consults it.
/// </summary>
public class TenantNotificationServiceTests
{
    private const string TenantId = "test-tenant-aaaa-bbbb-cccc-dddddddddddd";

    private static TenantNotificationService CreateService(IEnumerable<GlobalNotification> notifications)
    {
        var repo = new Mock<ITenantNotificationRepository>();
        repo.Setup(r => r.GetNotificationsAsync(TenantId, It.IsAny<int>()))
            .ReturnsAsync(notifications.ToList());
        return new TenantNotificationService(repo.Object, NullLogger<TenantNotificationService>.Instance);
    }

    private static GlobalNotification Notif(string type, string id) => new()
    {
        NotificationId = id,
        Type = type,
        Title = $"{type} title",
        Message = "msg",
        CreatedAt = DateTime.UtcNow,
        CreatedBy = "system",
    };

    [Fact]
    public async Task GetActive_AsMember_HidesAdminAudienceTypes()
    {
        var svc = CreateService(new[]
        {
            Notif("hardware_rejection", "n1"),       // Admin
            Notif("sla_breach", "n2"),               // Member
            Notif("sla_consecutive_failures", "n3"), // Member
        });

        var result = await svc.GetActiveNotificationsAsync(TenantId, NotificationAudience.Member);

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, n => n.Type == "hardware_rejection");
        Assert.Contains(result, n => n.Id == "n2");
        Assert.Contains(result, n => n.Id == "n3");
    }

    [Fact]
    public async Task GetActive_AsAdmin_SeesAllAudienceTypes()
    {
        var svc = CreateService(new[]
        {
            Notif("hardware_rejection", "n1"),
            Notif("sla_breach", "n2"),
        });

        var result = await svc.GetActiveNotificationsAsync(TenantId, NotificationAudience.Admin);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task GetActive_UnknownType_HiddenFromMember_VisibleToAdmin()
    {
        var notifs = new[] { Notif("brand_new_unregistered_type", "n1") };

        var asMember = await CreateService(notifs).GetActiveNotificationsAsync(TenantId, NotificationAudience.Member);
        var asAdmin = await CreateService(notifs).GetActiveNotificationsAsync(TenantId, NotificationAudience.Admin);

        Assert.Empty(asMember);
        Assert.Single(asAdmin);
    }
}
