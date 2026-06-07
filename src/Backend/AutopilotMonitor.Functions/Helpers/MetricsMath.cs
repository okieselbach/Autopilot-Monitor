using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// Shared statistical helpers for duration/SLA metrics aggregation.
/// </summary>
public static class MetricsMath
{
    /// <summary>
    /// Builds the complete app-metrics response object from a (pre-time-filtered) set of app
    /// install summaries. Single source of truth for both the tenant (<c>metrics/app</c>) and
    /// global (<c>global/metrics/app</c>) functions, which previously carried a verbatim copy of
    /// this GroupBy aggregation — keeping the Delivery Optimization rollup and the slowest/failing
    /// ranking in one place removes that drift risk.
    ///
    /// The Delivery Optimization rollup sums bytes across every row in an app group (not just the
    /// successful ones): DO telemetry is recorded during the download regardless of the install's
    /// final status. Peer bytes and Microsoft Connected Cache (MCC) bytes are reported separately —
    /// MCC is counted apart from peers by DO — and offload% credits both as "not pulled from the CDN".
    /// </summary>
    public static object BuildAppMetricsPayload(IEnumerable<AppInstallSummary> summaries)
    {
        var summaryList = summaries as IList<AppInstallSummary> ?? summaries.ToList();

        var appGroups = summaryList.GroupBy(s => s.AppName).Select(g =>
        {
            var completed = g.Where(s => s.Status == "Succeeded").ToList();
            var failed = g.Where(s => s.Status == "Failed").ToList();
            var total = g.Count();

            // DoAggregator is the single source for the DO rollup: it filters rows that actually
            // carry DO telemetry (DoDownloadMode >= 0) and falls back to peers + http when a legacy
            // row reports source bytes but no DoTotalBytesDownloaded — so that telemetry is not lost.
            var doG = DoAggregator.Compute(g);

            return new
            {
                appName = g.Key,
                totalInstalls = total,
                succeeded = completed.Count,
                failed = failed.Count,
                failureRate = total > 0 ? Math.Round((double)failed.Count / total * 100, 1) : 0,
                avgDurationSeconds = completed.Count > 0 ? Math.Round(completed.Average(s => s.DurationSeconds), 0) : 0,
                maxDurationSeconds = completed.Count > 0 ? completed.Max(s => s.DurationSeconds) : 0,
                avgDownloadBytes = completed.Count > 0 ? (long)completed.Average(s => s.DownloadBytes) : 0,
                doTotalBytesDownloaded = doG.TotalBytesDownloaded,
                doBytesFromPeers = doG.BytesFromPeers,
                doBytesFromCacheServer = doG.BytesFromCacheServer,
                doBytesFromHttp = doG.BytesFromHttp,
                peerOffloadPercent = OffloadPercent(doG.BytesFromPeers + doG.BytesFromCacheServer, doG.TotalBytesDownloaded),
                topFailureCodes = failed
                    .Where(f => !string.IsNullOrEmpty(f.FailureCode))
                    .GroupBy(f => f.FailureCode)
                    .OrderByDescending(fc => fc.Count())
                    .Take(3)
                    .Select(fc => new { code = fc.Key, count = fc.Count() })
            };
        }).ToList();

        var slowestApps = SelectSlowestApps(
            appGroups, a => a.succeeded, a => (double)a.avgDurationSeconds, minSamples: 3, take: 10);

        var topFailingApps = appGroups
            .Where(a => a.failed > 0)
            .OrderByDescending(a => a.failed)
            .ThenByDescending(a => a.failureRate)
            .Take(10)
            .ToList();

        var doAll = DoAggregator.Compute(summaryList);

        return new
        {
            success = true,
            totalApps = appGroups.Count,
            totalInstalls = summaryList.Count,
            slowestApps,
            topFailingApps,
            deliveryOptimization = new
            {
                totalBytesDownloaded = doAll.TotalBytesDownloaded,
                fromPeers = doAll.BytesFromPeers,
                fromCacheServer = doAll.BytesFromCacheServer,
                fromHttp = doAll.BytesFromHttp,
                peerOffloadPercent = OffloadPercent(doAll.BytesFromPeers + doAll.BytesFromCacheServer, doAll.TotalBytesDownloaded),
            }
        };
    }

    /// <summary>Share of total bytes (0-100, one decimal) not pulled from the CDN. 0 when no bytes.</summary>
    private static double OffloadPercent(long offloaded, long total)
        => total > 0 ? Math.Round((double)offloaded / total * 100, 1) : 0;

    /// <summary>
    /// Calculates the nearest-rank percentile of an ascending-sorted value list,
    /// rounded to one decimal place. Callers MUST pass values pre-sorted ascending.
    /// Returns 0 for an empty list.
    /// </summary>
    public static double Percentile(List<double> sortedValues, int percentile)
    {
        if (sortedValues.Count == 0) return 0;

        var index = (int)Math.Ceiling((percentile / 100.0) * sortedValues.Count) - 1;
        index = Math.Max(0, Math.Min(index, sortedValues.Count - 1));
        return Math.Round(sortedValues[index], 1);
    }

    /// <summary>
    /// Ranks apps slowest-first by average duration, after dropping any app with fewer than
    /// <paramref name="minSamples"/> successful installs. The sample floor stops a single N=1
    /// install (often unfinished, or a legacy pre-clamp row) from dominating the ranking as an
    /// artefact. Returns at most <paramref name="take"/> apps. Generic so both the tenant and
    /// global app-metrics functions can rank their anonymous projections without duplication.
    /// </summary>
    public static List<T> SelectSlowestApps<T>(
        IEnumerable<T> apps,
        Func<T, int> succeededSelector,
        Func<T, double> avgDurationSelector,
        int minSamples,
        int take)
    {
        return apps
            .Where(a => succeededSelector(a) >= minSamples)
            .OrderByDescending(avgDurationSelector)
            .Take(take)
            .ToList();
    }
}

/// <summary>
/// Per-tenant session status tally. Every status maps to exactly one bucket, so the component
/// counts always reconcile to <see cref="Total"/> by construction: Pending and Stalled — which
/// were previously counted in the total but in no bucket, silently widening the gap — now have
/// their own buckets, and any unrecognised status (incl. Unknown) lands in <see cref="Other"/>.
/// </summary>
public readonly record struct SessionStatusBuckets(
    int Total, int Succeeded, int Failed, int InProgress, int Pending, int Stalled, int Other)
{
    /// <summary>Returns a new tally with <paramref name="status"/> folded in.</summary>
    public SessionStatusBuckets Add(string? status)
    {
        var total = Total + 1;
        var succeeded = Succeeded + (status == "Succeeded" ? 1 : 0);
        var failed = Failed + (status == "Failed" ? 1 : 0);
        var inProgress = InProgress + (status == "InProgress" ? 1 : 0);
        var pending = Pending + (status == "Pending" ? 1 : 0);
        var stalled = Stalled + (status == "Stalled" ? 1 : 0);
        var other = total - (succeeded + failed + inProgress + pending + stalled);
        return new SessionStatusBuckets(total, succeeded, failed, inProgress, pending, stalled, other);
    }
}
