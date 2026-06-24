using System;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Shared OData predicate builder for the hybrid notification retention policy used by both the
    /// global (<see cref="TableNotificationRepository"/>) and tenant-scoped
    /// (<see cref="TableTenantNotificationRepository"/>) notification tables.
    /// <para>
    /// Policy: a <b>dismissed</b> row is prune-eligible once it was <i>dismissed</i> longer ago than
    /// the short <paramref name="dismissedCutoffUtc"/> (clock starts at <c>DismissedAt</c>, so the
    /// 30-day tail is measured from when the admin handled it, NOT from creation); any row (dismissed
    /// or unread) is prune-eligible once it was <i>created</i> longer ago than the long
    /// <paramref name="unreadCutoffUtc"/>. The long clause is the catch-all that bounds the table; the
    /// gap between the two cutoffs is exactly the window in which an unread (still-actionable) admin
    /// warning is preserved so it is never silently lost. Both dismiss paths in the repos always stamp
    /// <c>DismissedAt</c>, so a dismissed row missing it (e.g. inserted pre-dismissed) simply falls to
    /// the creation-age catch-all instead of being matched early.
    /// </para>
    /// Extracted as a pure function so the cutoff/active-vs-dismissed boundary logic is unit-testable
    /// without a live Table Storage endpoint (the row-delete loop around it is trivial mechanical code).
    /// </summary>
    internal static class NotificationRetentionFilter
    {
        /// <summary>OData datetime literal format expected by Azure Table Storage (UTC, second precision).</summary>
        internal static string FormatCutoff(DateTime cutoffUtc) =>
            $"datetime'{cutoffUtc.ToUniversalTime():yyyy-MM-ddTHH:mm:ss}Z'";

        /// <summary>
        /// Builds the retention predicate (without any PartitionKey clause). Callers AND-prepend their
        /// own partition scope where applicable. <paramref name="dismissedCutoffUtc"/> is compared
        /// against <c>DismissedAt</c> (dismissal age); <paramref name="unreadCutoffUtc"/> against
        /// <c>CreatedAt</c> (creation age).
        /// </summary>
        internal static string BuildPredicate(DateTime dismissedCutoffUtc, DateTime unreadCutoffUtc) =>
            $"((Dismissed eq true and DismissedAt lt {FormatCutoff(dismissedCutoffUtc)}) " +
            $"or CreatedAt lt {FormatCutoff(unreadCutoffUtc)})";
    }
}
