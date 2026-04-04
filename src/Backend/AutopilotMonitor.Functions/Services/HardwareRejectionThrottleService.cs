using System;
using System.Collections.Concurrent;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// In-memory throttle for hardware rejection webhook notifications.
    /// Ensures at most one notification per 24 hours per tenant+manufacturer+model combination.
    /// Registered as singleton; state resets on Function App restart (acceptable trade-off).
    /// </summary>
    public class HardwareRejectionThrottleService
    {
        private static readonly TimeSpan ThrottleWindow = TimeSpan.FromHours(24);
        private readonly ConcurrentDictionary<string, DateTime> _lastNotified = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Returns true if a notification should be sent for this combination.
        /// Thread-safe: concurrent calls for the same key may both return true on the first call,
        /// but subsequent calls within 24h will return false.
        /// </summary>
        public bool ShouldNotify(string tenantId, string? manufacturer, string? model)
        {
            var key = $"{tenantId}|{manufacturer ?? ""}|{model ?? ""}";
            var now = DateTime.UtcNow;

            if (_lastNotified.TryGetValue(key, out var lastTime) && (now - lastTime) < ThrottleWindow)
                return false;

            _lastNotified[key] = now;
            return true;
        }
    }
}
