using AutopilotMonitor.Functions.Helpers;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for <see cref="SignalRGroupHelper"/>. Locks in the parsing rules for the four
/// supported group formats so a future refactor cannot accidentally break tenant
/// isolation in <see cref="AutopilotMonitor.Functions.Functions.Infrastructure.SignalRAddToGroupFunction"/>,
/// which uses <see cref="SignalRGroupHelper.ExtractTenantIdFromGroupName"/> for cross-tenant validation.
/// </summary>
public class SignalRGroupHelperTests
{
    private const string TenantId = "11111111-2222-3333-4444-555555555555";
    private const string SessionId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    [Fact]
    public void ExtractTenantId_TenantWideGroup()
    {
        Assert.Equal(TenantId, SignalRGroupHelper.ExtractTenantIdFromGroupName($"tenant-{TenantId}"));
    }

    [Fact]
    public void ExtractTenantId_TenantNotifyMemberGroup()
    {
        Assert.Equal(TenantId, SignalRGroupHelper.ExtractTenantIdFromGroupName($"tenant-{TenantId}-notify-member"));
    }

    [Fact]
    public void ExtractTenantId_TenantNotifyAdminGroup()
    {
        Assert.Equal(TenantId, SignalRGroupHelper.ExtractTenantIdFromGroupName($"tenant-{TenantId}-notify-admin"));
    }

    [Fact]
    public void ExtractTenantId_SessionGroup()
    {
        Assert.Equal(TenantId, SignalRGroupHelper.ExtractTenantIdFromGroupName($"session-{TenantId}-{SessionId}"));
    }

    [Fact]
    public void ExtractTenantId_GlobalAdmins_ReturnsNull()
    {
        Assert.Null(SignalRGroupHelper.ExtractTenantIdFromGroupName("global-admins"));
    }

    [Fact]
    public void ExtractTenantId_UnknownFormat_ReturnsNull()
    {
        Assert.Null(SignalRGroupHelper.ExtractTenantIdFromGroupName("foo-bar"));
    }

    [Fact]
    public void IsTenantNotifyAdminGroup_OnlyTrueForAdminSuffix()
    {
        Assert.True(SignalRGroupHelper.IsTenantNotifyAdminGroup($"tenant-{TenantId}-notify-admin"));
        Assert.False(SignalRGroupHelper.IsTenantNotifyAdminGroup($"tenant-{TenantId}-notify-member"));
        Assert.False(SignalRGroupHelper.IsTenantNotifyAdminGroup($"tenant-{TenantId}"));
        Assert.False(SignalRGroupHelper.IsTenantNotifyAdminGroup("global-admins"));
    }

    [Fact]
    public void GroupNameBuilders_RoundTrip()
    {
        Assert.Equal($"tenant-{TenantId}-notify-member", SignalRGroupHelper.TenantNotifyMemberGroup(TenantId));
        Assert.Equal($"tenant-{TenantId}-notify-admin", SignalRGroupHelper.TenantNotifyAdminGroup(TenantId));
    }
}
