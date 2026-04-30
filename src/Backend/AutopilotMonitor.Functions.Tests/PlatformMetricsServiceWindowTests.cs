using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Verifies PlatformMetricsService passes the requested window through to GetAllSessionsAsync
/// and surfaces it on the response.
/// </summary>
public class PlatformMetricsServiceWindowTests
{
    private static (PlatformMetricsService Service, Mock<ISessionRepository> Repo) CreateService()
    {
        var sessionRepo = new Mock<ISessionRepository>();
        sessionRepo
            .Setup(r => r.GetAllSessionsAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<int?>()))
            .ReturnsAsync(new SessionPage { Sessions = new List<SessionSummary>(), HasMore = false });
        sessionRepo
            .Setup(r => r.GetSessionEventsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new List<EnrollmentEvent>());

        var service = new PlatformMetricsService(sessionRepo.Object, NullLogger<PlatformMetricsService>.Instance);
        return (service, sessionRepo);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(13)]
    [InlineData(17)]
    [InlineData(64)]
    public async Task ComputePlatformMetrics_passes_days_through_to_session_repo(int days)
    {
        var (service, repo) = CreateService();

        int? capturedDays = null;
        repo
            .Setup(r => r.GetAllSessionsAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<int?>()))
            .Callback<int, string?, int?>((_, _, d) => capturedDays = d)
            .ReturnsAsync(new SessionPage { Sessions = new List<SessionSummary>(), HasMore = false });

        var result = await service.ComputePlatformMetricsAsync(days);

        Assert.Equal(days, capturedDays);
        Assert.Equal(days, result.WindowDays);
    }

    [Fact]
    public async Task ComputePlatformMetrics_clamps_zero_to_one()
    {
        var (service, repo) = CreateService();

        int? capturedDays = null;
        repo
            .Setup(r => r.GetAllSessionsAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<int?>()))
            .Callback<int, string?, int?>((_, _, d) => capturedDays = d)
            .ReturnsAsync(new SessionPage { Sessions = new List<SessionSummary>(), HasMore = false });

        var result = await service.ComputePlatformMetricsAsync(0);

        Assert.Equal(1, capturedDays);
        Assert.Equal(1, result.WindowDays);
    }

    [Fact]
    public async Task ComputePlatformMetrics_clamps_excessive_to_365()
    {
        var (service, _) = CreateService();
        var result = await service.ComputePlatformMetricsAsync(99999);
        Assert.Equal(365, result.WindowDays);
    }
}
