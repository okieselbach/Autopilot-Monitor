using AutopilotMonitor.Functions.Telemetry;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Locks in the filtering contract of <see cref="StorageDependencyFilterProcessor"/>: successful
/// Azure Storage dependencies (Table/Queue/Blob) are dropped to curb AppDependencies ingestion
/// cost, while FAILED storage calls and ALL non-storage telemetry (HTTP/Graph/requests) survive.
/// </summary>
public class StorageDependencyFilterProcessorTests
{
    /// <summary>Capturing terminal processor — records everything forwarded to it.</summary>
    private sealed class CapturingProcessor : ITelemetryProcessor
    {
        public List<ITelemetry> Received { get; } = new();
        public void Process(ITelemetry item) => Received.Add(item);
    }

    private static (StorageDependencyFilterProcessor processor, CapturingProcessor next) Build()
    {
        var next = new CapturingProcessor();
        return (new StorageDependencyFilterProcessor(next), next);
    }

    private static DependencyTelemetry Dep(string target, bool? success, string? data = null)
        => new() { Target = target, Success = success, Data = data ?? string.Empty };

    [Theory]
    [InlineData("myacct.table.core.windows.net")]
    [InlineData("myacct.queue.core.windows.net")]
    [InlineData("myacct.blob.core.windows.net")]
    public void SuccessfulStorageDependency_IsDropped(string target)
    {
        var (processor, next) = Build();
        processor.Process(Dep(target, success: true));
        Assert.Empty(next.Received);
    }

    [Fact]
    public void SuccessfulStorageDependency_WithNullSuccess_IsDropped()
    {
        // Null Success (no explicit outcome) is treated as non-failure → still noise.
        var (processor, next) = Build();
        processor.Process(Dep("myacct.table.core.windows.net", success: null));
        Assert.Empty(next.Received);
    }

    [Fact]
    public void FailedStorageDependency_IsKept()
    {
        var (processor, next) = Build();
        var dep = Dep("myacct.queue.core.windows.net", success: false);
        processor.Process(dep);
        Assert.Same(dep, Assert.Single(next.Received));
    }

    [Theory]
    [InlineData("graph.microsoft.com")]
    [InlineData("login.microsoftonline.com")]
    [InlineData("api.nvd.nist.gov")]
    public void NonStorageDependency_IsKept(string target)
    {
        var (processor, next) = Build();
        var dep = Dep(target, success: true);
        processor.Process(dep);
        Assert.Same(dep, Assert.Single(next.Received));
    }

    [Fact]
    public void StorageEndpointInDataField_IsDropped()
    {
        // Some instrumentation modes put the endpoint in Data rather than Target.
        var (processor, next) = Build();
        processor.Process(Dep(target: string.Empty, success: true, data: "GET https://myacct.blob.core.windows.net/container/x"));
        Assert.Empty(next.Received);
    }

    [Fact]
    public void NonDependencyTelemetry_IsAlwaysKept()
    {
        var (processor, next) = Build();
        var request = new RequestTelemetry { Name = "POST /api/agent/telemetry", Success = true };
        var trace = new TraceTelemetry { Message = "hello" };
        processor.Process(request);
        processor.Process(trace);
        Assert.Equal(2, next.Received.Count);
    }
}
