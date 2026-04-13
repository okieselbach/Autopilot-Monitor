using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Metrics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for SLA metrics computation.
/// Validates success rate, P95 calculation, monthly grouping, and violator detection.
/// </summary>
public class SlaMetricsServiceTests
{
    // Use unique tenant IDs per test to avoid static cache collisions
    private static string NewTenantId() => $"sla-test-{Guid.NewGuid():N}";

    private static (SlaMetricsService Service, string TenantId) CreateService(
        List<SessionSummary> sessions,
        TenantConfiguration? config = null,
        List<AppInstallSummary>? appInstalls = null)
    {
        var tenantId = config?.TenantId ?? NewTenantId();
        // Ensure all sessions have the correct tenant ID
        foreach (var s in sessions) s.TenantId = tenantId;

        var maintenanceRepo = new Mock<IMaintenanceRepository>();
        maintenanceRepo.Setup(r => r.GetSessionsByDateRangeAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), tenantId))
            .ReturnsAsync(sessions);

        var metricsRepo = new Mock<IMetricsRepository>();
        metricsRepo.Setup(r => r.GetAppInstallSummariesByTenantAsync(tenantId))
            .ReturnsAsync(appInstalls ?? new List<AppInstallSummary>());

        var configService = new Mock<TenantConfigurationService>(
            Mock.Of<IConfigRepository>(),
            NullLogger<TenantConfigurationService>.Instance,
            new MemoryCache(new MemoryCacheOptions()));
        configService.Setup(c => c.GetConfigurationAsync(tenantId))
            .ReturnsAsync(config ?? CreateDefaultConfig(tenantId));

        var logger = NullLogger<SlaMetricsService>.Instance;

        return (new SlaMetricsService(maintenanceRepo.Object, metricsRepo.Object, configService.Object, logger), tenantId);
    }

    private static TenantConfiguration CreateDefaultConfig(
        string? tenantId = null,
        decimal? targetSuccessRate = 95m,
        int? targetMaxDuration = 60)
    {
        return new TenantConfiguration
        {
            TenantId = tenantId ?? NewTenantId(),
            SlaTargetSuccessRate = targetSuccessRate,
            SlaTargetMaxDurationMinutes = targetMaxDuration,
        };
    }

    private static SessionSummary CreateSession(
        SessionStatus status,
        int? durationSeconds = null,
        DateTime? startedAt = null)
    {
        var started = startedAt ?? DateTime.UtcNow.AddHours(-1);
        return new SessionSummary
        {
            SessionId = Guid.NewGuid().ToString(),
            TenantId = "placeholder", // overwritten by CreateService
            DeviceName = "TEST-DEVICE",
            SerialNumber = "SN-001",
            Status = status,
            StartedAt = started,
            CompletedAt = durationSeconds.HasValue ? started.AddSeconds(durationSeconds.Value) : null,
            DurationSeconds = durationSeconds,
        };
    }

    [Fact]
    public async Task ComputeSlaMetrics_AllSucceeded_Returns100Percent()
    {
        var sessions = new List<SessionSummary>
        {
            CreateSession(SessionStatus.Succeeded, 1800),
            CreateSession(SessionStatus.Succeeded, 2400),
            CreateSession(SessionStatus.Succeeded, 3000),
        };

        var (service, tenantId) = CreateService(sessions);
        var result = await service.ComputeSlaMetricsAsync(tenantId, 1);

        Assert.Equal(100, result.CurrentMonth.SuccessRate);
        Assert.True(result.CurrentMonth.SuccessRateMet);
        Assert.Empty(result.Violators.Where(v => v.ViolationType == "Failed"));
    }

    [Fact]
    public async Task ComputeSlaMetrics_MixedResults_CorrectRate()
    {
        var sessions = new List<SessionSummary>
        {
            CreateSession(SessionStatus.Succeeded, 1800),
            CreateSession(SessionStatus.Succeeded, 2400),
            CreateSession(SessionStatus.Succeeded, 3000),
            CreateSession(SessionStatus.Failed, 1200),
        };

        var (service, tenantId) = CreateService(sessions);
        var result = await service.ComputeSlaMetricsAsync(tenantId, 1);

        Assert.Equal(75, result.CurrentMonth.SuccessRate);
        Assert.False(result.CurrentMonth.SuccessRateMet);
        Assert.Equal(3, result.CurrentMonth.Succeeded);
        Assert.Equal(1, result.CurrentMonth.Failed);
    }

    [Fact]
    public async Task ComputeSlaMetrics_NoSessions_ZeroRate()
    {
        var (service, tenantId) = CreateService(new List<SessionSummary>());
        var result = await service.ComputeSlaMetricsAsync(tenantId, 1);

        Assert.Equal(0, result.CurrentMonth.SuccessRate);
        Assert.Equal(0, result.CurrentMonth.TotalCompleted);
        Assert.Empty(result.Violators);
    }

    [Fact]
    public async Task ComputeSlaMetrics_DurationViolations_DetectedCorrectly()
    {
        var config = CreateDefaultConfig(targetMaxDuration: 30); // 30 min target
        var sessions = new List<SessionSummary>
        {
            CreateSession(SessionStatus.Succeeded, 1200), // 20 min - ok
            CreateSession(SessionStatus.Succeeded, 2400), // 40 min - violation
            CreateSession(SessionStatus.Succeeded, 3000), // 50 min - violation
        };

        var (service, tenantId) = CreateService(sessions, config);
        var result = await service.ComputeSlaMetricsAsync(tenantId, 1);

        Assert.Equal(2, result.CurrentMonth.DurationViolationCount);
        Assert.Equal(2, result.Violators.Count(v => v.ViolationType == "DurationExceeded"));
    }

    [Fact]
    public async Task ComputeSlaMetrics_FailedSession_IsViolator()
    {
        var sessions = new List<SessionSummary>
        {
            CreateSession(SessionStatus.Succeeded, 1800),
            CreateSession(SessionStatus.Failed, 600),
        };

        var (service, tenantId) = CreateService(sessions);
        var result = await service.ComputeSlaMetricsAsync(tenantId, 1);

        var failedViolator = result.Violators.FirstOrDefault(v => v.ViolationType == "Failed");
        Assert.NotNull(failedViolator);
    }

    [Fact]
    public async Task ComputeSlaMetrics_NoTarget_AlwaysMet()
    {
        var config = CreateDefaultConfig(targetSuccessRate: null, targetMaxDuration: null);
        var sessions = new List<SessionSummary>
        {
            CreateSession(SessionStatus.Failed, 1200),
        };

        var (service, tenantId) = CreateService(sessions, config);
        var result = await service.ComputeSlaMetricsAsync(tenantId, 1);

        Assert.True(result.CurrentMonth.SuccessRateMet);
        Assert.True(result.CurrentMonth.DurationTargetMet);
    }

    [Fact]
    public async Task ComputeSlaMetrics_TargetEcho_ReflectsConfig()
    {
        var config = CreateDefaultConfig(targetSuccessRate: 99.5m, targetMaxDuration: 45);
        config.SlaTargetAppInstallSuccessRate = 98m;

        var (service, tenantId) = CreateService(new List<SessionSummary>(), config);
        var result = await service.ComputeSlaMetricsAsync(tenantId, 1);

        Assert.Equal(99.5m, result.TargetSuccessRate);
        Assert.Equal(45, result.TargetMaxDurationMinutes);
        Assert.Equal(98m, result.TargetAppInstallSuccessRate);
    }

    [Fact]
    public async Task ComputeSlaMetrics_ViolatorsLimitedTo100()
    {
        var sessions = Enumerable.Range(0, 150)
            .Select(_ => CreateSession(SessionStatus.Failed, 600))
            .ToList();

        var (service, tenantId) = CreateService(sessions);
        var result = await service.ComputeSlaMetricsAsync(tenantId, 1);

        Assert.Equal(100, result.Violators.Count);
    }
}
