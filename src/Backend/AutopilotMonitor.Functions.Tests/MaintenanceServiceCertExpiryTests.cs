using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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

    // --- EvaluateBucket (bucket-level: only the freshest cert drives the alarm) ---
    // Correctness invariant under test: an old cert in the same role bucket must NOT
    // generate its own warning as long as a fresh successor is present. The bucket is
    // judged by its newest member's NotAfter, because that's when "no chain helper
    // ever again" actually arrives.

    [Fact]
    public void EvaluateBucket_EmptyBucket_ReturnsNullEverything()
    {
        var eval = MaintenanceService.EvaluateBucket(Array.Empty<X509Certificate2>(), DateTime.UtcNow);

        Assert.Null(eval.EventType);
        Assert.Null(eval.Freshest);
    }

    [Fact]
    public void EvaluateBucket_SingleFreshCert_StaysSilent()
    {
        var now = new DateTime(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc);
        using var fresh = MakeCert("CN=Fresh Root", notAfter: now.AddDays(180));

        var eval = MaintenanceService.EvaluateBucket(new[] { fresh }, now);

        Assert.Null(eval.EventType);
        Assert.Equal(fresh.Thumbprint, eval.Freshest!.Thumbprint);
    }

    [Fact]
    public void EvaluateBucket_SingleSoonCert_FiresExpiringSoon()
    {
        var now = new DateTime(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc);
        using var soon = MakeCert("CN=Soon", notAfter: now.AddDays(60));

        var eval = MaintenanceService.EvaluateBucket(new[] { soon }, now);

        Assert.Equal("EmbeddedCertExpiringSoon", eval.EventType);
        Assert.Equal(soon.Thumbprint, eval.Freshest!.Thumbprint);
        Assert.Equal(60, eval.DaysUntilExpiry);
    }

    [Fact]
    public void EvaluateBucket_OldExpiredAndFreshSuccessor_StaysSilent()
    {
        // The user's correctness ask: bundle has both intune-intermediate-mdm-2026
        // (expired) AND intune-intermediate-mdm-2028 (fresh). The old one is just a
        // chain helper for in-flight device certs; the bucket is healthy because the
        // successor is in place. NO alarm should fire.
        var now = new DateTime(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc);
        using var oldExpired = MakeCert("CN=Old Intermediate", notAfter: now.AddDays(-3));
        using var freshSuccessor = MakeCert("CN=New Intermediate", notAfter: now.AddDays(800));

        var eval = MaintenanceService.EvaluateBucket(new[] { oldExpired, freshSuccessor }, now);

        Assert.Null(eval.EventType);
        Assert.Equal(freshSuccessor.Thumbprint, eval.Freshest!.Thumbprint);
    }

    [Fact]
    public void EvaluateBucket_OldExpiredAndSoonSuccessor_FiresOnSuccessor()
    {
        // Bundle has an expired old + a soon-expiring "newest". The alarm fires for
        // the soon one (it IS the bucket's successor, so its expiry IS the bucket's
        // expiry). Critical for the old cert is suppressed.
        var now = new DateTime(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc);
        using var oldExpired = MakeCert("CN=Old", notAfter: now.AddDays(-30));
        using var soonSuccessor = MakeCert("CN=Soon Successor", notAfter: now.AddDays(50));

        var eval = MaintenanceService.EvaluateBucket(new[] { oldExpired, soonSuccessor }, now);

        Assert.Equal("EmbeddedCertExpiringSoon", eval.EventType);
        Assert.Equal(soonSuccessor.Thumbprint, eval.Freshest!.Thumbprint);
    }

    [Fact]
    public void EvaluateBucket_AllExpired_FiresExpiredOnFreshestExpired()
    {
        // Edge case: bundle has only expired certs, no successor. Freshest is still
        // the latest-expiring one. Alarm fires Critical, message reflects the freshest.
        var now = new DateTime(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc);
        using var ancient = MakeCert("CN=Ancient", notAfter: now.AddDays(-100));
        using var recent = MakeCert("CN=Recent", notAfter: now.AddDays(-3));

        var eval = MaintenanceService.EvaluateBucket(new[] { ancient, recent }, now);

        Assert.Equal("EmbeddedCertExpired", eval.EventType);
        Assert.Equal(recent.Thumbprint, eval.Freshest!.Thumbprint);
        Assert.True(eval.DaysUntilExpiry < 0);
    }

    [Fact]
    public void EvaluateBucket_TwoFreshSuccessors_StaysSilentOnLatest()
    {
        // Bundle has two valid successors (e.g. a current root and a future root).
        // Both are far from expiry; pick the latest by NotAfter.
        var now = new DateTime(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc);
        using var nearFuture = MakeCert("CN=2026 Root", notAfter: new DateTime(2026, 8, 12, 0, 0, 0, DateTimeKind.Utc));
        using var farFuture = MakeCert("CN=2030 Root", notAfter: new DateTime(2030, 9, 15, 0, 0, 0, DateTimeKind.Utc));

        var eval = MaintenanceService.EvaluateBucket(new[] { nearFuture, farFuture }, now);

        Assert.Null(eval.EventType);
        Assert.Equal(farFuture.Thumbprint, eval.Freshest!.Thumbprint);
    }

    private static X509Certificate2 MakeCert(string subjectCn, DateTime notAfter, DateTime? notBefore = null)
    {
        // Self-signed cert factory for unit tests. The validator's chain check is
        // not part of this code path - we only care about NotBefore/NotAfter and
        // Thumbprint for bucket evaluation.
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={subjectCn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var nb = (notBefore ?? notAfter.AddYears(-1)).ToUniversalTime();
        return req.CreateSelfSigned(new DateTimeOffset(nb, TimeSpan.Zero), new DateTimeOffset(notAfter.ToUniversalTime(), TimeSpan.Zero));
    }
}
