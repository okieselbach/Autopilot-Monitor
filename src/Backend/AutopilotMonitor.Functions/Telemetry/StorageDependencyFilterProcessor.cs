using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;

namespace AutopilotMonitor.Functions.Telemetry;

/// <summary>
/// Drops successful Azure Storage dependency telemetry (Table / Queue / Blob) before it
/// reaches Application Insights, to curb AppDependencies ingestion cost.
///
/// Rationale: this backend is storage-I/O heavy (telemetry ingest, index dual-write, queues,
/// diagnostics blobs), so the overwhelming majority of AppDependencies rows are high-frequency,
/// successful storage calls with little diagnostic value. AppDependencies does NOT support the
/// cheaper Basic table plan, so the only lever is reducing what is emitted.
///
/// Deliberately scoped:
/// - Only <see cref="DependencyTelemetry"/> is considered; requests, traces, exceptions,
///   metrics and all NON-storage dependencies (HTTP, Microsoft Graph, SQL, SignalR, ...) pass
///   through untouched.
/// - FAILED storage calls are KEPT (Success == false): they are rare and are real signal for
///   troubleshooting throttling / transient faults, so the noise reduction does not blind us.
///
/// Registered in Program.cs via AddApplicationInsightsTelemetryProcessor so it runs inside the
/// isolated worker's telemetry pipeline, where the app's own Azure SDK dependencies are tracked.
/// </summary>
public sealed class StorageDependencyFilterProcessor : ITelemetryProcessor
{
    // Azure Storage data-plane endpoints. Matching on the dependency Target host is robust
    // across SDK versions and instrumentation modes (classic "Azure blob"/"Azure table" types
    // vs. newer Azure SDK ActivitySource namespaces both resolve to these hosts).
    private static readonly string[] StorageEndpointSuffixes =
    {
        ".table.core.windows.net",
        ".queue.core.windows.net",
        ".blob.core.windows.net",
    };

    private readonly ITelemetryProcessor _next;

    public StorageDependencyFilterProcessor(ITelemetryProcessor next) => _next = next;

    public void Process(ITelemetry item)
    {
        if (ShouldDrop(item))
        {
            // Swallow: not forwarded to the next processor → never sent → never billed.
            return;
        }

        _next.Process(item);
    }

    private static bool ShouldDrop(ITelemetry item)
    {
        if (item is not DependencyTelemetry dependency)
        {
            return false;
        }

        // Keep failures — rare, high-value diagnostic signal. Only successful storage chatter is noise.
        if (dependency.Success == false)
        {
            return false;
        }

        return IsStorageEndpoint(dependency.Target) || IsStorageEndpoint(dependency.Data);
    }

    private static bool IsStorageEndpoint(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var suffix in StorageEndpointSuffixes)
        {
            if (value.Contains(suffix, System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
