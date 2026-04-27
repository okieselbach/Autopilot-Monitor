#nullable enable
using System.Collections.Generic;
using System.Linq;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime
{
    /// <summary>
    /// Builds the <c>Data</c> dictionary for <c>app_tracking_summary</c> events. Used by the
    /// per-transition snapshot in <see cref="SignalAdapters.ImeLogTrackerAdapter"/>
    /// (event-driven, fires on every terminal app-state change) and by the terminal emit in
    /// <see cref="Termination.EnrollmentTerminationHandler"/>.
    /// <para>
    /// Schema is the flat V1 shape — easy to read, easy to query, no nested objects:
    /// <list type="bullet">
    ///   <item>Aggregates: <c>totalApps</c>, <c>completedApps</c>, <c>errorCount</c>,
    ///         <c>deviceErrors</c>, <c>userErrors</c>, <c>hasErrors</c>,
    ///         <c>isAllCompleted</c>, <c>ignoredCount</c></item>
    ///   <item>State counters: <c>installed</c>, <c>skipped</c>, <c>failed</c>,
    ///         <c>postponed</c>, <c>downloading</c>, <c>installing</c>, <c>pending</c></item>
    ///   <item>App-name lists per bucket: <c>installedNames</c>, <c>failedNames</c>,
    ///         <c>skippedNames</c>, <c>postponedNames</c>, <c>pendingNames</c></item>
    /// </list>
    /// </para>
    /// </summary>
    internal static class AppTrackingSummaryBuilder
    {
        public static Dictionary<string, object> Build(
            IReadOnlyList<AppPackageState>? packages,
            int ignoredCount = 0)
        {
            var totalApps = 0;
            var installed = 0;
            var skipped = 0;
            var postponed = 0;
            var failed = 0;
            var downloading = 0;
            var installing = 0;
            var deviceErrors = 0;
            var userErrors = 0;

            var installedNames = new List<string>();
            var skippedNames = new List<string>();
            var failedNames = new List<string>();
            var postponedNames = new List<string>();
            var pendingNames = new List<string>();

            if (packages != null)
            {
                foreach (var pkg in packages)
                {
                    totalApps++;
                    var name = pkg.Name ?? string.Empty;

                    switch (pkg.InstallationState)
                    {
                        case AppInstallationState.Installed:
                            installed++;
                            installedNames.Add(name);
                            break;
                        case AppInstallationState.Skipped:
                            skipped++;
                            skippedNames.Add(name);
                            break;
                        case AppInstallationState.Postponed:
                            postponed++;
                            postponedNames.Add(name);
                            break;
                        case AppInstallationState.Error:
                            failed++;
                            failedNames.Add(name);
                            if (pkg.Targeted == AppTargeted.Device) deviceErrors++;
                            else if (pkg.Targeted == AppTargeted.User) userErrors++;
                            break;
                        case AppInstallationState.Downloading:
                            downloading++;
                            break;
                        case AppInstallationState.Installing:
                        case AppInstallationState.InProgress:
                            installing++;
                            break;
                        default:
                            // Unknown / NotInstalled — counted as pending below; capture name so
                            // the UI can show "still pending: X, Y, Z" without re-deriving.
                            pendingNames.Add(name);
                            break;
                    }
                }
            }

            var completedApps = installed + skipped + postponed + failed;
            var pending = totalApps - completedApps - downloading - installing;
            if (pending < 0) pending = 0;

            return new Dictionary<string, object>(System.StringComparer.Ordinal)
            {
                ["totalApps"] = totalApps,
                ["completedApps"] = completedApps,
                ["errorCount"] = failed,
                ["deviceErrors"] = deviceErrors,
                ["userErrors"] = userErrors,
                ["hasErrors"] = failed > 0,
                ["isAllCompleted"] = totalApps > 0 && completedApps == totalApps,
                ["ignoredCount"] = ignoredCount,
                ["downloading"] = downloading,
                ["installing"] = installing,
                ["installed"] = installed,
                ["skipped"] = skipped,
                ["failed"] = failed,
                ["postponed"] = postponed,
                ["pending"] = pending,
                ["pendingNames"] = pendingNames,
                ["failedNames"] = failedNames,
                ["postponedNames"] = postponedNames,
                ["installedNames"] = installedNames,
                ["skippedNames"] = skippedNames,
            };
        }
    }
}
