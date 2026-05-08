using AutopilotMonitor.Functions.Services;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tier-boundary + dedup-key tests for the embedded-cert expiry watcher.
/// Full-service smoke tests would require constructing 14 dependencies; these
/// pure-function tests cover the boundary risks (off-by-one, wrong tier) that
/// are most likely to break the watcher silently.
/// </summary>
public class MaintenanceServiceCertExpiryTests
{
    // --- ClassifyCertExpiryTier ---
    // Thresholds (per MaintenanceService.CertExpiry.cs):
    //   <= 7d  -> EmbeddedCertExpired (Critical)
    //   <= 30d -> EmbeddedCertExpiringUrgent (Error)
    //   <= 90d -> EmbeddedCertExpiringSoon (Warning)
    //   else   -> null (silent)

    [Theory]
    [InlineData(91, null)]
    [InlineData(180, null)]
    [InlineData(3650, null)]
    public void ClassifyCertExpiryTier_FarFromExpiry_ReturnsNull(int days, string? expected)
    {
        Assert.Equal(expected, MaintenanceService.ClassifyCertExpiryTier(days));
    }

    [Theory]
    [InlineData(90)]
    [InlineData(60)]
    [InlineData(31)]
    public void ClassifyCertExpiryTier_WithinSoonWindow_ReturnsExpiringSoon(int days)
    {
        Assert.Equal("EmbeddedCertExpiringSoon", MaintenanceService.ClassifyCertExpiryTier(days));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(15)]
    [InlineData(8)]
    public void ClassifyCertExpiryTier_WithinUrgentWindow_ReturnsExpiringUrgent(int days)
    {
        Assert.Equal("EmbeddedCertExpiringUrgent", MaintenanceService.ClassifyCertExpiryTier(days));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-365)]
    public void ClassifyCertExpiryTier_WithinCriticalWindowOrExpired_ReturnsExpired(int days)
    {
        Assert.Equal("EmbeddedCertExpired", MaintenanceService.ClassifyCertExpiryTier(days));
    }

    [Fact]
    public void ClassifyCertExpiryTier_BoundaryAt91_IsAboveSoonTier()
    {
        Assert.Null(MaintenanceService.ClassifyCertExpiryTier(91));
    }

    [Fact]
    public void ClassifyCertExpiryTier_BoundaryAt31_IsSoonNotUrgent()
    {
        Assert.Equal("EmbeddedCertExpiringSoon", MaintenanceService.ClassifyCertExpiryTier(31));
    }

    [Fact]
    public void ClassifyCertExpiryTier_BoundaryAt8_IsUrgentNotExpired()
    {
        Assert.Equal("EmbeddedCertExpiringUrgent", MaintenanceService.ClassifyCertExpiryTier(8));
    }

    // --- ExtractThumbprint (used to build the dedup key from existing OpsEvents) ---

    [Fact]
    public void ExtractThumbprint_WithValidJson_ReturnsThumbprint()
    {
        var json = """{"role":"Root","subject":"CN=Foo","thumbprint":"ABC123","daysUntilExpiry":45}""";
        Assert.Equal("ABC123", MaintenanceService.ExtractThumbprint(json));
    }

    [Fact]
    public void ExtractThumbprint_WithMissingProperty_ReturnsEmpty()
    {
        var json = """{"role":"Root","subject":"CN=Foo"}""";
        Assert.Equal("", MaintenanceService.ExtractThumbprint(json));
    }

    [Fact]
    public void ExtractThumbprint_WithNull_ReturnsEmpty()
    {
        Assert.Equal("", MaintenanceService.ExtractThumbprint(null));
    }

    [Fact]
    public void ExtractThumbprint_WithEmpty_ReturnsEmpty()
    {
        Assert.Equal("", MaintenanceService.ExtractThumbprint(""));
    }

    [Fact]
    public void ExtractThumbprint_WithMalformedJson_ReturnsEmpty()
    {
        Assert.Equal("", MaintenanceService.ExtractThumbprint("not json {"));
    }

    [Fact]
    public void ExtractThumbprint_WithNumericThumbprint_ReturnsEmpty()
    {
        // Defensive: real OpsEvent details always serialize thumbprint as string,
        // but if a non-string value sneaks in we must not crash or mis-key.
        var json = """{"thumbprint":12345}""";
        Assert.Equal("", MaintenanceService.ExtractThumbprint(json));
    }
}
