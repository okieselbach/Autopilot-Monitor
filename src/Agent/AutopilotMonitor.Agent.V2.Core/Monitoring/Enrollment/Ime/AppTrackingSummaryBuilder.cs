#nullable enable
using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime
{
    /// <summary>
    /// Builds the <c>Data</c> dictionary for <c>app_tracking_summary</c> events.
    /// Used both by the per-transition snapshot in
    /// <see cref="SignalAdapters.ImeLogTrackerAdapter"/> (event-driven, fires on every
    /// terminal app-state change) and by the terminal emit in
    /// <see cref="Termination.EnrollmentTerminationHandler"/>.
    /// <para>
    /// Schema (V2 with live buckets):
    /// <list type="bullet">
    ///   <item>Aggregates: <c>totalApps</c>, <c>completedApps</c></item>
    ///   <item>Final buckets: <c>installedApps</c>, <c>skippedApps</c>, <c>postponedApps</c>, <c>failedApps</c></item>
    ///   <item>Live buckets: <c>downloading</c>, <c>installing</c>, <c>pending</c></item>
    ///   <item>Detail (terminal-only by default): <c>byPhase</c> + <c>perApp</c></item>
    /// </list>
    /// </para>
    /// <para>
    /// Per-transition snapshots set <paramref name="includePerAppDetail"/> to <c>false</c> to
    /// keep storage cost O(N) instead of O(N²) — the per-app rows duplicate timing data on
    /// every snapshot otherwise. The terminal emit keeps detail to drive the SummaryDialog
    /// and post-mortem diagnostics in the Web UI.
    /// </para>
    /// </summary>
    internal static class AppTrackingSummaryBuilder
    {
        public static Dictionary<string, object> Build(
            IReadOnlyList<AppPackageState>? packages,
            IReadOnlyDictionary<string, AppInstallTiming>? timings,
            bool includePerAppDetail = true)
        {
            var totalApps = 0;
            var installedApps = 0;
            var skippedApps = 0;
            var postponedApps = 0;
            var failedApps = 0;
            var downloading = 0;
            var installing = 0;
            var byPhase = includePerAppDetail
                ? new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal)
                : null;
            var perApp = includePerAppDetail
                ? new List<Dictionary<string, object>>()
                : null;

            if (packages != null)
            {
                foreach (var pkg in packages)
                {
                    totalApps++;
                    switch (pkg.InstallationState)
                    {
                        case AppInstallationState.Installed: installedApps++; break;
                        case AppInstallationState.Skipped: skippedApps++; break;
                        case AppInstallationState.Postponed: postponedApps++; break;
                        case AppInstallationState.Error: failedApps++; break;
                        case AppInstallationState.Downloading: downloading++; break;
                        case AppInstallationState.Installing:
                        case AppInstallationState.InProgress: installing++; break;
                    }

                    if (!includePerAppDetail) continue;

                    var phaseKey = pkg.Targeted.ToString();
                    if (!byPhase!.TryGetValue(phaseKey, out var bucket))
                    {
                        bucket = new Dictionary<string, int>(StringComparer.Ordinal)
                        {
                            ["total"] = 0, ["installed"] = 0, ["skipped"] = 0, ["postponed"] = 0, ["failed"] = 0,
                        };
                        byPhase[phaseKey] = bucket;
                    }
                    bucket["total"]++;
                    if (pkg.InstallationState == AppInstallationState.Installed) bucket["installed"]++;
                    else if (pkg.InstallationState == AppInstallationState.Skipped) bucket["skipped"]++;
                    else if (pkg.InstallationState == AppInstallationState.Postponed) bucket["postponed"]++;
                    else if (pkg.InstallationState == AppInstallationState.Error) bucket["failed"]++;

                    AppInstallTiming? timing = null;
                    timings?.TryGetValue(pkg.Id, out timing);
                    var appEntry = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["appId"] = pkg.Id,
                        ["appName"] = pkg.Name ?? string.Empty,
                        ["phase"] = phaseKey,
                        ["finalState"] = pkg.InstallationState.ToString(),
                    };
                    if (timing?.StartedAtUtc != null) appEntry["startedAt"] = timing.StartedAtUtc.Value.ToString("o");
                    if (timing?.CompletedAtUtc != null) appEntry["completedAt"] = timing.CompletedAtUtc.Value.ToString("o");
                    if (timing?.DurationSeconds != null) appEntry["durationSeconds"] = timing.DurationSeconds.Value;
                    perApp!.Add(appEntry);
                }
            }

            var completedApps = installedApps + skippedApps + postponedApps + failedApps;
            var pending = totalApps - completedApps - downloading - installing;
            if (pending < 0) pending = 0;

            var data = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["totalApps"] = totalApps,
                ["completedApps"] = completedApps,
                ["installedApps"] = installedApps,
                ["skippedApps"] = skippedApps,
                ["postponedApps"] = postponedApps,
                ["failedApps"] = failedApps,
                ["downloading"] = downloading,
                ["installing"] = installing,
                ["pending"] = pending,
            };
            if (includePerAppDetail)
            {
                data["byPhase"] = byPhase!;
                data["perApp"] = perApp!;
            }
            return data;
        }
    }
}
