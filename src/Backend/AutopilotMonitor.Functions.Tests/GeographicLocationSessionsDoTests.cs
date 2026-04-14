using AutopilotMonitor.Functions.Functions.Metrics;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Regression guard: the geographic drilldown must surface per-session DO telemetry
/// so operators can troubleshoot which devices contributed to a region's DO % badge.
/// Formula must stay consistent with GetGeographicMetricsFunction (peers / total * 100,
/// preferring DoTotalBytesDownloaded as denominator, falling back to peers+http).
/// </summary>
public class GeographicLocationSessionsDoTests
{
    private static AppInstallSummary App(string sessionId, int downloadMode, long peers, long http, long total,
        long lan = 0, long group = 0, long internetPeers = 0)
    {
        return new AppInstallSummary
        {
            SessionId = sessionId,
            DoDownloadMode = downloadMode,
            DoBytesFromPeers = peers,
            DoBytesFromHttp = http,
            DoTotalBytesDownloaded = total,
            DoBytesFromLanPeers = lan,
            DoBytesFromGroupPeers = group,
            DoBytesFromInternetPeers = internetPeers,
        };
    }

    private static SessionSummary Session(string id) => new SessionSummary { SessionId = id };

    [Fact]
    public void BuildRows_SessionWithDoAndNonDoApps_ComputesWeightedPercent()
    {
        var sessions = new List<SessionSummary> { Session("S1") };
        var apps = new List<AppInstallSummary>
        {
            App("S1", downloadMode: 0, peers: 50_000_000, http: 50_000_000, total: 100_000_000),
            App("S1", downloadMode: 1, peers: 0, http: 100_000_000, total: 100_000_000),
            App("S1", downloadMode: -1, peers: 0, http: 0, total: 0), // no DO telemetry
        };

        var rows = GetGeographicLocationSessionsFunction.BuildRows(sessions, apps);

        Assert.Single(rows);
        var row = rows[0];
        Assert.True(row.HasDoTelemetry);
        Assert.Equal(2, row.DoAppCount);
        Assert.Equal(3, row.TotalAppCount);
        // 50MB peers / 200MB total = 25%
        Assert.Equal(25.0, row.DoPercentPeerCaching);
        Assert.Equal(50_000_000, row.DoBytesFromPeers);
        Assert.Equal(150_000_000, row.DoBytesFromHttp);
        Assert.Equal(200_000_000, row.DoTotalBytesDownloaded);
    }

    [Fact]
    public void BuildRows_SessionWithoutAppData_MarkedNoTelemetry()
    {
        var rows = GetGeographicLocationSessionsFunction.BuildRows(
            new List<SessionSummary> { Session("S2") },
            new List<AppInstallSummary>());

        Assert.Single(rows);
        Assert.False(rows[0].HasDoTelemetry);
        Assert.Equal(0, rows[0].DoAppCount);
        Assert.Equal(0, rows[0].TotalAppCount);
        Assert.Equal(0, rows[0].DoPercentPeerCaching);
    }

    [Fact]
    public void BuildRows_FallsBackToPeersPlusHttp_WhenTotalBytesMissing()
    {
        // Legacy records may not populate DoTotalBytesDownloaded — must fall back
        // to peers + http so the denominator isn't zero and the % is still meaningful.
        var apps = new List<AppInstallSummary>
        {
            App("S3", downloadMode: 0, peers: 40_000_000, http: 60_000_000, total: 0),
        };
        var rows = GetGeographicLocationSessionsFunction.BuildRows(
            new List<SessionSummary> { Session("S3") }, apps);

        Assert.True(rows[0].HasDoTelemetry);
        Assert.Equal(40.0, rows[0].DoPercentPeerCaching);
        Assert.Equal(100_000_000, rows[0].DoTotalBytesDownloaded);
    }

    [Fact]
    public void BuildRows_MultipleSessions_AggregatesMatchRegionFormula()
    {
        // Invariant: sum(session peers) / sum(session total bytes) across the drilldown
        // must equal the region-level AvgDoPercentPeerCaching computed by
        // GetGeographicMetricsFunction from the same app summaries.
        var sessions = new List<SessionSummary> { Session("A"), Session("B"), Session("C") };
        var apps = new List<AppInstallSummary>
        {
            App("A", 0, peers: 30_000_000, http: 70_000_000, total: 100_000_000),
            App("B", 0, peers: 80_000_000, http: 20_000_000, total: 100_000_000),
            App("C", -1, 0, 0, 0),
        };

        var rows = GetGeographicLocationSessionsFunction.BuildRows(sessions, apps);
        var withDo = rows.Where(r => r.HasDoTelemetry).ToList();
        var totalPeers = withDo.Sum(r => r.DoBytesFromPeers);
        var totalBytes = withDo.Sum(r => r.DoTotalBytesDownloaded);
        var weighted = totalBytes > 0 ? (double)totalPeers / totalBytes * 100 : 0;

        Assert.Equal(2, withDo.Count);
        Assert.Equal(55.0, weighted, precision: 6);
    }
}
