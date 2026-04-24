#nullable enable
using System;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime
{
    /// <summary>
    /// Per-app install-lifecycle timestamps captured by <see cref="SignalAdapters.ImeLogTrackerAdapter"/>.
    /// Plan §5 Fix 4 — powers the <c>StartedAt</c> / <c>CompletedAt</c> / <c>DurationSeconds</c>
    /// fields on <c>app_install_*</c> events, the <c>FinalStatusPackageInfo</c> timing rows, and
    /// the <c>app_tracking_summary</c> terminal event.
    /// <para>
    /// <b>StartedAtUtc</b> is recorded on the first lifecycle transition into Downloading /
    /// Installing / InProgress. <b>CompletedAtUtc</b> is recorded on the first transition to a
    /// terminal state (Installed / Skipped / Postponed / Error). Both are "set-once" — subsequent
    /// identical-state events do not overwrite an earlier stamp.
    /// </para>
    /// </summary>
    public sealed class AppInstallTiming
    {
        public AppInstallTiming(DateTime? startedAtUtc, DateTime? completedAtUtc)
        {
            StartedAtUtc = startedAtUtc;
            CompletedAtUtc = completedAtUtc;
        }

        public DateTime? StartedAtUtc { get; }

        public DateTime? CompletedAtUtc { get; }

        /// <summary>
        /// Elapsed time from first Downloading/Installing transition to terminal state, in
        /// seconds. <c>null</c> when either endpoint is missing (e.g. app still installing,
        /// or the adapter missed the start event because it came online mid-install).
        /// </summary>
        public double? DurationSeconds =>
            StartedAtUtc.HasValue && CompletedAtUtc.HasValue
                ? (CompletedAtUtc.Value - StartedAtUtc.Value).TotalSeconds
                : (double?)null;
    }
}
