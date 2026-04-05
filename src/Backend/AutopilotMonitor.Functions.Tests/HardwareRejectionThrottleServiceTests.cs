using AutopilotMonitor.Functions.Services;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for the hardware rejection notification throttle.
///
/// CORRECTNESS GUARD: This service prevents duplicate webhook notifications for
/// the same hardware model. The throttle must be thread-safe — a race condition
/// here causes duplicate Teams/Slack alerts for every concurrent distress report.
/// </summary>
public class HardwareRejectionThrottleServiceTests
{
    private static readonly string TenantA = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private static readonly string TenantB = "11111111-2222-3333-4444-555555555555";

    private static HardwareRejectionThrottleService CreateService() => new();

    // =========================================================================
    // Basic throttle behavior
    // =========================================================================

    [Fact]
    public void ShouldNotify_FirstCall_ReturnsTrue()
    {
        var svc = CreateService();

        var result = svc.ShouldNotify(TenantA, "Dell", "Latitude 5520");

        Assert.True(result);
    }

    [Fact]
    public void ShouldNotify_SecondCallSameKey_ReturnsFalse()
    {
        var svc = CreateService();

        var first = svc.ShouldNotify(TenantA, "Dell", "Latitude 5520");
        var second = svc.ShouldNotify(TenantA, "Dell", "Latitude 5520");

        Assert.True(first);
        Assert.False(second);
    }

    // =========================================================================
    // Key isolation
    // =========================================================================

    [Fact]
    public void ShouldNotify_DifferentHardware_AreIndependent()
    {
        var svc = CreateService();

        var dell = svc.ShouldNotify(TenantA, "Dell", "Latitude 5520");
        var hp = svc.ShouldNotify(TenantA, "HP", "EliteBook 840");

        Assert.True(dell);
        Assert.True(hp);
    }

    [Fact]
    public void ShouldNotify_DifferentTenants_AreIndependent()
    {
        var svc = CreateService();

        var tenantA = svc.ShouldNotify(TenantA, "Dell", "Latitude 5520");
        var tenantB = svc.ShouldNotify(TenantB, "Dell", "Latitude 5520");

        Assert.True(tenantA);
        Assert.True(tenantB);
    }

    // =========================================================================
    // Case insensitivity (StringComparer.OrdinalIgnoreCase on dictionary)
    // =========================================================================

    [Fact]
    public void ShouldNotify_CaseInsensitive_Throttled()
    {
        var svc = CreateService();

        var upper = svc.ShouldNotify(TenantA, "DELL", "LATITUDE 5520");
        var lower = svc.ShouldNotify(TenantA, "dell", "latitude 5520");

        Assert.True(upper);
        Assert.False(lower);
    }

    // =========================================================================
    // Null parameters
    // =========================================================================

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "Latitude 5520")]
    [InlineData("Dell", null)]
    public void ShouldNotify_NullParams_FirstTrueSecondFalse(string? manufacturer, string? model)
    {
        var svc = CreateService();

        var first = svc.ShouldNotify(TenantA, manufacturer, model);
        var second = svc.ShouldNotify(TenantA, manufacturer, model);

        Assert.True(first);
        Assert.False(second);
    }

    // =========================================================================
    // Concurrency: validates the AddOrUpdate fix (no TOCTOU race)
    // =========================================================================

    [Fact]
    public async Task ShouldNotify_ConcurrentCalls_ExactlyOneReturnsTrue()
    {
        var svc = CreateService();
        var barrier = new ManualResetEventSlim(false);
        int trueCount = 0;
        const int threadCount = 50;

        var tasks = Enumerable.Range(0, threadCount).Select(_ => Task.Run(() =>
        {
            barrier.Wait();
            if (svc.ShouldNotify(TenantA, "Dell", "Latitude 5520"))
                Interlocked.Increment(ref trueCount);
        })).ToArray();

        barrier.Set();
        await Task.WhenAll(tasks);

        Assert.Equal(1, trueCount);
    }
}
