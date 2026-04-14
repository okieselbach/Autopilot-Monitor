using System.Collections.Generic;
using System.Linq;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Computes Delivery Optimization peer-caching aggregates from a collection of
    /// <see cref="AppInstallSummary"/> records. Single source of truth shared between
    /// the region-level aggregate (GetGeographicMetricsFunction) and the per-session
    /// aggregate surfaced by the drilldown endpoint.
    /// </summary>
    public static class DoAggregator
    {
        public class DoAggregate
        {
            public int DoAppCount { get; set; }
            public long BytesFromPeers { get; set; }
            public long BytesFromHttp { get; set; }
            public long TotalBytesDownloaded { get; set; }
            public long BytesFromLanPeers { get; set; }
            public long BytesFromGroupPeers { get; set; }
            public long BytesFromInternetPeers { get; set; }

            public bool HasTelemetry => DoAppCount > 0;
            public double PercentPeerCaching => TotalBytesDownloaded > 0
                ? (double)BytesFromPeers / TotalBytesDownloaded * 100
                : 0;
        }

        public static DoAggregate Compute(IEnumerable<AppInstallSummary> apps)
        {
            var doApps = apps.Where(a => a.DoDownloadMode >= 0).ToList();
            var peers = doApps.Sum(a => a.DoBytesFromPeers);
            var http = doApps.Sum(a => a.DoBytesFromHttp);
            var downloaded = doApps.Sum(a => a.DoTotalBytesDownloaded);
            return new DoAggregate
            {
                DoAppCount = doApps.Count,
                BytesFromPeers = peers,
                BytesFromHttp = http,
                TotalBytesDownloaded = downloaded > 0 ? downloaded : (peers + http),
                BytesFromLanPeers = doApps.Sum(a => a.DoBytesFromLanPeers),
                BytesFromGroupPeers = doApps.Sum(a => a.DoBytesFromGroupPeers),
                BytesFromInternetPeers = doApps.Sum(a => a.DoBytesFromInternetPeers),
            };
        }
    }
}
