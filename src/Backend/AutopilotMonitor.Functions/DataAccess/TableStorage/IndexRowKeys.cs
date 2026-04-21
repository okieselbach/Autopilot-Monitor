using System;
using System.Globalization;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Pure PK/RK builders for the 5 V2 Decision Engine index tables
    /// (Plan §2.8 query matrix, §M5.d). State-less + zero-IO so every key shape is
    /// unit-testable against canned inputs without an Azurite harness.
    /// </summary>
    internal static class IndexRowKeys
    {
        private const string TimestampFormat = "yyyyMMddHHmmss";

        // Azure Tables forbids these characters in PK/RK values. Discriminators are typically
        // enum names (safe ASCII); the sanitize step is defensive cover for free-form strings
        // like DeadEndReason that could in principle include a slash.
        private static readonly char[] ForbiddenKeyChars = { '/', '\\', '#', '?' };

        // ============================================================ SessionsByTerminal

        public static string BuildSessionsByTerminalPk(string tenantId, string terminalStage)
            => $"{tenantId}_{Sanitize(terminalStage)}";

        public static string BuildSessionsByTerminalRk(DateTime occurredAtUtc, string sessionId)
            => $"{FormatTimestamp(occurredAtUtc)}_{sessionId}";

        // ============================================================ SessionsByStage

        public static string BuildSessionsByStagePk(string tenantId, string stage)
            => $"{tenantId}_{Sanitize(stage)}";

        /// <summary>
        /// Ticks are zero-padded to 19 digits (DateTime.MaxValue has 19-digit ticks) so
        /// lexicographic RK ordering matches numeric time order.
        /// </summary>
        public static string BuildSessionsByStageRk(DateTime lastUpdatedUtc, string sessionId)
            => $"{lastUpdatedUtc.ToUniversalTime().Ticks.ToString("D19", CultureInfo.InvariantCulture)}_{sessionId}";

        // ============================================================ DeadEndsByReason

        public static string BuildDeadEndsByReasonPk(string tenantId, string deadEndReason)
            => $"{tenantId}_{Sanitize(deadEndReason)}";

        public static string BuildDeadEndsByReasonRk(DateTime occurredAtUtc, string sessionId, int stepIndex)
            => $"{FormatTimestamp(occurredAtUtc)}_{sessionId}_{stepIndex.ToString("D6", CultureInfo.InvariantCulture)}";

        // ============================================================ ClassifierVerdictsByIdLevel

        public static string BuildClassifierVerdictsByIdLevelPk(string tenantId, string classifierId, string hypothesisLevel)
            => $"{tenantId}_{Sanitize(classifierId)}_{Sanitize(hypothesisLevel)}";

        public static string BuildClassifierVerdictsByIdLevelRk(DateTime occurredAtUtc, string sessionId, int stepIndex)
            => $"{FormatTimestamp(occurredAtUtc)}_{sessionId}_{stepIndex.ToString("D6", CultureInfo.InvariantCulture)}";

        // ============================================================ SignalsByKind

        public static string BuildSignalsByKindPk(string tenantId, string signalKind)
            => $"{tenantId}_{Sanitize(signalKind)}";

        public static string BuildSignalsByKindRk(DateTime occurredAtUtc, string sessionId, long sessionSignalOrdinal)
            => $"{FormatTimestamp(occurredAtUtc)}_{sessionId}_{sessionSignalOrdinal.ToString("D10", CultureInfo.InvariantCulture)}";

        // ============================================================ Helpers

        private static string FormatTimestamp(DateTime utc)
            => utc.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);

        /// <summary>
        /// Replaces characters Azure Tables disallows in PK/RK values with <c>_</c>. Enum-derived
        /// discriminators never trigger this; it's defensive cover for free-form strings.
        /// </summary>
        internal static string Sanitize(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (value.IndexOfAny(ForbiddenKeyChars) < 0) return value;

            var buffer = new char[value.Length];
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                buffer[i] = Array.IndexOf(ForbiddenKeyChars, c) >= 0 ? '_' : c;
            }
            return new string(buffer);
        }
    }
}
