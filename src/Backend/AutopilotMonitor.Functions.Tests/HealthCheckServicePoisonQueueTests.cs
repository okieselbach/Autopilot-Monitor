using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Monitoring;
using AutopilotMonitor.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Boundary + integration tests for the poison-queue health check. Pure-function
/// tests cover the tier classifier; service-level tests use an in-memory probe to
/// drive <c>CheckPoisonQueuesAsync</c> directly so we don't have to stand up the
/// 5 other dependencies just to exercise the queue path.
/// </summary>
public class HealthCheckServicePoisonQueueTests
{
    // --- ClassifyPoisonQueueTier ---
    // Defaults (per HealthCheckService): warn=1, critical=10
    //   count >= critical -> Critical
    //   count >= warning  -> Warning
    //   else              -> None

    [Theory]
    [InlineData(0L)]
    public void Classify_ZeroCount_ReturnsNone(long count)
    {
        Assert.Equal(
            HealthCheckService.PoisonQueueTier.None,
            HealthCheckService.ClassifyPoisonQueueTier(count,
                HealthCheckService.DefaultPoisonQueueWarningThreshold,
                HealthCheckService.DefaultPoisonQueueCriticalThreshold));
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(5L)]
    [InlineData(9L)]
    public void Classify_InWarningBand_ReturnsWarning(long count)
    {
        Assert.Equal(
            HealthCheckService.PoisonQueueTier.Warning,
            HealthCheckService.ClassifyPoisonQueueTier(count,
                HealthCheckService.DefaultPoisonQueueWarningThreshold,
                HealthCheckService.DefaultPoisonQueueCriticalThreshold));
    }

    [Theory]
    [InlineData(10L)]
    [InlineData(100L)]
    [InlineData(10_000L)]
    public void Classify_AtOrAboveCritical_ReturnsCritical(long count)
    {
        Assert.Equal(
            HealthCheckService.PoisonQueueTier.Critical,
            HealthCheckService.ClassifyPoisonQueueTier(count,
                HealthCheckService.DefaultPoisonQueueWarningThreshold,
                HealthCheckService.DefaultPoisonQueueCriticalThreshold));
    }

    [Fact]
    public void Classify_CustomThresholds_RespectsConfig()
    {
        Assert.Equal(HealthCheckService.PoisonQueueTier.None,
            HealthCheckService.ClassifyPoisonQueueTier(4, 5, 50));
        Assert.Equal(HealthCheckService.PoisonQueueTier.Warning,
            HealthCheckService.ClassifyPoisonQueueTier(5, 5, 50));
        Assert.Equal(HealthCheckService.PoisonQueueTier.Warning,
            HealthCheckService.ClassifyPoisonQueueTier(49, 5, 50));
        Assert.Equal(HealthCheckService.PoisonQueueTier.Critical,
            HealthCheckService.ClassifyPoisonQueueTier(50, 5, 50));
    }

    // --- CheckPoisonQueuesAsync via fake probe ---

    [Fact]
    public async Task Check_AllPoisonQueuesEmpty_ReportsHealthy()
    {
        var probe = new FakePoisonQueueProbe();
        foreach (var q in HealthCheckService.MonitoredPoisonQueues)
            probe.Counts[q] = 0;

        var svc = BuildService(probe);
        var check = await svc.CheckPoisonQueuesAsync();

        Assert.Equal("Poison Queues", check.Name);
        Assert.Equal("healthy", check.Status);
        Assert.Equal("All poison queues empty", check.Message);
        Assert.Equal(HealthCheckService.MonitoredPoisonQueues.Length, check.Details!.Count);
        Assert.All(check.Details.Values, v => Assert.Equal("0 messages", v));
    }

    [Fact]
    public async Task Check_SingleQueueWithBacklog_ReportsWarning()
    {
        var probe = new FakePoisonQueueProbe();
        probe.Counts[Constants.QueueNames.AnalyzeOnEnrollmentEnd + "-poison"] = 3;
        probe.Counts[Constants.QueueNames.VulnerabilityCorrelate + "-poison"] = 0;
        probe.Counts[Constants.QueueNames.TelemetryIndexReconcile + "-poison"] = 0;

        var svc = BuildService(probe);
        var check = await svc.CheckPoisonQueuesAsync();

        Assert.Equal("warning", check.Status);
        Assert.Equal("Operator review required — failed messages parked after 5 retries", check.Message);
        Assert.Equal("3 messages",
            check.Details![Constants.QueueNames.AnalyzeOnEnrollmentEnd + "-poison"]);
        Assert.Equal("0 messages",
            check.Details[Constants.QueueNames.VulnerabilityCorrelate + "-poison"]);
    }

    [Fact]
    public async Task Check_QueueAboveCritical_ReportsUnhealthy()
    {
        var probe = new FakePoisonQueueProbe();
        probe.Counts[Constants.QueueNames.AnalyzeOnEnrollmentEnd + "-poison"] = 15;
        probe.Counts[Constants.QueueNames.VulnerabilityCorrelate + "-poison"] = 0;
        probe.Counts[Constants.QueueNames.TelemetryIndexReconcile + "-poison"] = 2;

        var svc = BuildService(probe);
        var check = await svc.CheckPoisonQueuesAsync();

        Assert.Equal("unhealthy", check.Status);
        Assert.Contains("Poison backlog accumulating", check.Message);
        Assert.Equal("15 messages",
            check.Details![Constants.QueueNames.AnalyzeOnEnrollmentEnd + "-poison"]);
    }

    [Fact]
    public async Task Check_ProbeThrows_DetailsRecordErrorAndForceWarning()
    {
        var probe = new FakePoisonQueueProbe
        {
            ExceptionFor =
            {
                [Constants.QueueNames.VulnerabilityCorrelate + "-poison"] =
                    new InvalidOperationException("storage 500")
            }
        };
        probe.Counts[Constants.QueueNames.AnalyzeOnEnrollmentEnd + "-poison"] = 0;
        probe.Counts[Constants.QueueNames.TelemetryIndexReconcile + "-poison"] = 0;

        var svc = BuildService(probe);
        var check = await svc.CheckPoisonQueuesAsync();

        Assert.Equal("warning", check.Status);
        var errorEntry = (string)check.Details![Constants.QueueNames.VulnerabilityCorrelate + "-poison"];
        Assert.StartsWith("error:", errorEntry);
        Assert.Contains("storage 500", errorEntry);
    }

    [Fact]
    public async Task Check_CustomThresholdsFromConfig_AreRespected()
    {
        var probe = new FakePoisonQueueProbe();
        probe.Counts[Constants.QueueNames.AnalyzeOnEnrollmentEnd + "-poison"] = 4;
        probe.Counts[Constants.QueueNames.VulnerabilityCorrelate + "-poison"] = 0;
        probe.Counts[Constants.QueueNames.TelemetryIndexReconcile + "-poison"] = 0;

        // warn=5 → 4 messages now classifies as healthy (would be warning under defaults)
        var svc = BuildService(probe, new Dictionary<string, string?>
        {
            ["PoisonQueueWarningThreshold"] = "5",
            ["PoisonQueueCriticalThreshold"] = "50",
        });

        var check = await svc.CheckPoisonQueuesAsync();
        Assert.Equal("healthy", check.Status);
    }

    [Fact]
    public async Task Check_OneMessage_FormatsAsSingularInDetails()
    {
        var probe = new FakePoisonQueueProbe();
        probe.Counts[Constants.QueueNames.AnalyzeOnEnrollmentEnd + "-poison"] = 1;
        probe.Counts[Constants.QueueNames.VulnerabilityCorrelate + "-poison"] = 0;
        probe.Counts[Constants.QueueNames.TelemetryIndexReconcile + "-poison"] = 0;

        var svc = BuildService(probe);
        var check = await svc.CheckPoisonQueuesAsync();
        Assert.Equal("1 message",
            check.Details![Constants.QueueNames.AnalyzeOnEnrollmentEnd + "-poison"]);
    }

    [Fact]
    public void MonitoredPoisonQueues_CoversAllProducerQueues()
    {
        // Guard against silently adding a new producer queue without a poison entry.
        // If a fourth queue is introduced, MonitoredPoisonQueues + this assertion must move together.
        Assert.Equal(3, HealthCheckService.MonitoredPoisonQueues.Length);
        Assert.Contains(
            Constants.QueueNames.AnalyzeOnEnrollmentEnd + "-poison",
            HealthCheckService.MonitoredPoisonQueues);
        Assert.Contains(
            Constants.QueueNames.VulnerabilityCorrelate + "-poison",
            HealthCheckService.MonitoredPoisonQueues);
        Assert.Contains(
            Constants.QueueNames.TelemetryIndexReconcile + "-poison",
            HealthCheckService.MonitoredPoisonQueues);
    }

    // --- helpers ---

    /// <summary>
    /// Builds a HealthCheckService wired only with the dependencies that
    /// <see cref="HealthCheckService.CheckPoisonQueuesAsync"/> touches.
    /// The other constructor parameters are passed as <c>null!</c> — they
    /// belong to sibling checks that this test class never invokes.
    /// </summary>
    private static HealthCheckService BuildService(
        FakePoisonQueueProbe probe,
        IDictionary<string, string?>? extraConfig = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(extraConfig ?? new Dictionary<string, string?>())
            .Build();

        return new HealthCheckService(
            NullLogger<HealthCheckService>.Instance,
            adminConfigService: null!,
            httpClientFactory: null!,
            metricsReader: null!,
            poisonQueueProbe: probe,
            configuration: config);
    }

    private sealed class FakePoisonQueueProbe : IPoisonQueueProbe
    {
        public Dictionary<string, long> Counts { get; } = new();
        public Dictionary<string, Exception> ExceptionFor { get; } = new();

        public Task<long> GetApproximateMessageCountAsync(string queueName, CancellationToken ct)
        {
            if (ExceptionFor.TryGetValue(queueName, out var ex))
                throw ex;
            if (Counts.TryGetValue(queueName, out var count))
                return Task.FromResult(count);
            return Task.FromResult(0L);
        }
    }
}
