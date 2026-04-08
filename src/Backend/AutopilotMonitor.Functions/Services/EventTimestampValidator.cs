using System;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Validates and sanitizes event timestamps from agent devices.
    /// Timestamps outside the reasonable range are clamped (not rejected) to preserve event data.
    /// When clamping occurs, callers should set OriginalTimestamp + TimestampClamped on the event
    /// so that the original value remains available for troubleshooting and root-cause analysis.
    /// </summary>
    internal static class EventTimestampValidator
    {
        /// <summary>
        /// Maximum clock skew tolerance for agent-side timestamps ahead of server time.
        /// Agents may have slightly fast clocks; 24 hours covers timezone misconfiguration too.
        /// </summary>
        internal const int MaxFutureToleranceHours = 24;

        /// <summary>
        /// Maximum clock skew tolerance for agent-side timestamps BEHIND server time, relative
        /// to the ingest receive time. Anything older than this is treated as a clock-skew bug
        /// and clamped to the server's receive time. 7 days (168h) covers legitimate spool replay
        /// after short outages while still catching devices whose hardware clock is weeks in the
        /// past (observed in field: 18-day drift causing ghost "excessive data sender" blocks).
        ///
        /// Rationale for 168h (not tighter):
        /// - EventSpool has no max-age and could theoretically hold events from earlier sessions.
        /// - WhiteGlove Part-2 events are freshly stamped by the restarted agent → not affected.
        /// - Pre-provisioned sessions are already excluded from excessive-data detection.
        ///
        /// This constant is also the effective lower bound for sanitized timestamps: any value
        /// older than utcNow − 168h (including catastrophic cases like DateTime.MinValue) is
        /// clamped to utcNow. There is no separate "floor" constant — past-drift subsumes it.
        /// </summary>
        internal const int MaxPastToleranceHours = 168;

        /// <summary>
        /// Default maximum duration in seconds (7 days) for SafeDurationSeconds clamping.
        /// </summary>
        internal const int DefaultMaxDurationSeconds = 604800;

        /// <summary>
        /// Ensures a DateTime has UTC kind. Mirrors the EnsureUtc logic in TableStorageService
        /// but is available without an instance. Called internally by SanitizeTimestamp.
        /// </summary>
        internal static DateTime EnsureUtc(DateTime dt) => dt.Kind switch
        {
            DateTimeKind.Utc => dt,
            DateTimeKind.Local => dt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc)
        };

        /// <summary>
        /// Checks whether a timestamp falls within the reasonable range.
        /// Valid range: [utcNow − 168h, utcNow + 24h].
        /// </summary>
        internal static bool IsReasonableTimestamp(DateTime timestamp, DateTime utcNow)
        {
            var utcTs = EnsureUtc(timestamp);
            return utcTs >= utcNow.AddHours(-MaxPastToleranceHours)
                && utcTs <= utcNow.AddHours(MaxFutureToleranceHours);
        }

        /// <summary>
        /// Sanitizes a timestamp by ensuring UTC kind and clamping to the valid range.
        /// Clamping rules:
        /// - Below utcNow − 168h → clamped to utcNow (past-drift from bad device clock; server receive
        ///   time is the best fallback because the event definitely arrived "now"). This also catches
        ///   catastrophic values like DateTime.MinValue.
        /// - Above utcNow + 24h → clamped to utcNow (future-drift, same rationale).
        /// Returns the original value unchanged if it is within the valid range.
        /// </summary>
        internal static DateTime SanitizeTimestamp(DateTime timestamp, DateTime utcNow)
        {
            var utcTs = EnsureUtc(timestamp);

            if (utcTs < utcNow.AddHours(-MaxPastToleranceHours))
                return EnsureUtc(utcNow);

            if (utcTs > utcNow.AddHours(MaxFutureToleranceHours))
                return EnsureUtc(utcNow);

            return utcTs;
        }

        /// <summary>
        /// Computes the duration in seconds between two timestamps, clamped to [0, maxDurationSeconds].
        /// Prevents int overflow from extreme timestamp differences (e.g. DateTime.MinValue to DateTime.MaxValue).
        /// Returns 0 if end is before start.
        /// </summary>
        internal static int SafeDurationSeconds(DateTime start, DateTime end, int maxDurationSeconds = DefaultMaxDurationSeconds)
        {
            if (end <= start)
                return 0;

            var totalSeconds = (end - start).TotalSeconds;

            if (totalSeconds > maxDurationSeconds)
                return maxDurationSeconds;

            return (int)totalSeconds;
        }

        /// <summary>
        /// Produces a safe RowKey timestamp segment by sanitizing the timestamp first,
        /// then formatting as yyyyMMddHHmmssfff.
        /// </summary>
        internal static string SafeRowKeyTimestamp(DateTime timestamp, DateTime utcNow)
        {
            var sanitized = SanitizeTimestamp(timestamp, utcNow);
            return sanitized.ToString("yyyyMMddHHmmssfff");
        }
    }
}
