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
        /// Earliest reasonable timestamp — the product did not exist before this date.
        /// </summary>
        internal static readonly DateTime MinReasonableTimestamp = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// Maximum clock skew tolerance for agent-side timestamps ahead of server time.
        /// Agents may have slightly fast clocks; 24 hours covers timezone misconfiguration too.
        /// </summary>
        internal const int MaxFutureToleranceHours = 24;

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
        /// Checks whether a timestamp falls within the reasonable range [MinReasonableTimestamp, utcNow + 24h].
        /// </summary>
        internal static bool IsReasonableTimestamp(DateTime timestamp, DateTime utcNow)
        {
            var utcTs = EnsureUtc(timestamp);
            return utcTs >= MinReasonableTimestamp && utcTs <= utcNow.AddHours(MaxFutureToleranceHours);
        }

        /// <summary>
        /// Sanitizes a timestamp by ensuring UTC kind and clamping to the valid range.
        /// - Below MinReasonableTimestamp → clamped to MinReasonableTimestamp
        /// - Above utcNow + 24h → clamped to utcNow (server receive time as best-effort fallback)
        /// Returns the original value unchanged if it is within the valid range.
        /// </summary>
        internal static DateTime SanitizeTimestamp(DateTime timestamp, DateTime utcNow)
        {
            var utcTs = EnsureUtc(timestamp);

            if (utcTs < MinReasonableTimestamp)
                return MinReasonableTimestamp;

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
