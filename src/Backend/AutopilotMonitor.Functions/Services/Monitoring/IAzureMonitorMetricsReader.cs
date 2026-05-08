using System;
using System.Threading;
using System.Threading.Tasks;

namespace AutopilotMonitor.Functions.Services.Monitoring
{
    /// <summary>
    /// Thin abstraction over Azure Monitor metrics so watchers can be unit-tested
    /// without spinning up a real <c>MetricsQueryClient</c>. Reads are scoped to a
    /// single Azure resource (e.g. an Azure SignalR Service instance).
    /// </summary>
    public interface IAzureMonitorMetricsReader
    {
        /// <summary>
        /// Returns the maximum value observed for <paramref name="metricName"/> on
        /// <paramref name="resourceId"/> within the trailing <paramref name="window"/>
        /// (now − window … now). Null when no data points were returned.
        /// </summary>
        Task<double?> GetMaximumAsync(
            string resourceId, string metricName, TimeSpan window, CancellationToken ct);

        /// <summary>
        /// Returns the total sum of <paramref name="metricName"/> on
        /// <paramref name="resourceId"/> across the inclusive UTC range
        /// [<paramref name="from"/>, <paramref name="to"/>]. Null when no data
        /// points were returned.
        /// </summary>
        Task<double?> GetTotalAsync(
            string resourceId, string metricName, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
    }
}
