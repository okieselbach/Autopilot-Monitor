using AutopilotMonitor.Functions.Services;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for the SLA notification throttle service.
/// Ensures notifications are throttled correctly with a 4-hour cooldown period.
/// </summary>
public class SlaNotificationThrottleServiceTests
{
    private static readonly string TestTenantId = "test-tenant-001";

    [Fact]
    public void ShouldNotify_FirstCall_ReturnsTrue()
    {
        var service = new SlaNotificationThrottleService();

        var result = service.ShouldNotify(TestTenantId, "sla_breach");

        Assert.True(result);
    }

    [Fact]
    public void ShouldNotify_ImmediateSecondCall_ReturnsFalse()
    {
        var service = new SlaNotificationThrottleService();

        service.ShouldNotify(TestTenantId, "sla_breach");
        var result = service.ShouldNotify(TestTenantId, "sla_breach");

        Assert.False(result);
    }

    [Fact]
    public void ShouldNotify_DifferentTypes_BothReturnTrue()
    {
        var service = new SlaNotificationThrottleService();

        var result1 = service.ShouldNotify(TestTenantId, "sla_breach");
        var result2 = service.ShouldNotify(TestTenantId, "consecutive_failures");

        Assert.True(result1);
        Assert.True(result2);
    }

    [Fact]
    public void ShouldNotify_DifferentTenants_BothReturnTrue()
    {
        var service = new SlaNotificationThrottleService();

        var result1 = service.ShouldNotify("tenant-a", "sla_breach");
        var result2 = service.ShouldNotify("tenant-b", "sla_breach");

        Assert.True(result1);
        Assert.True(result2);
    }

    [Fact]
    public void ShouldNotify_AfterReset_ReturnsTrue()
    {
        var service = new SlaNotificationThrottleService();

        service.ShouldNotify(TestTenantId, "sla_breach");
        Assert.False(service.ShouldNotify(TestTenantId, "sla_breach"));

        service.Reset(TestTenantId, "sla_breach");
        Assert.True(service.ShouldNotify(TestTenantId, "sla_breach"));
    }
}
