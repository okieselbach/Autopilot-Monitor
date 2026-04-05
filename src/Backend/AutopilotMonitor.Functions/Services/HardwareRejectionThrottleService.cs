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
        /// Thread-safe: uses AddOrUpdate to atomically check the throttle window and claim
        /// the notification slot. At most one concurrent caller per key wins.
        /// </summary>
        public bool ShouldNotify(string tenantId, string? manufacturer, string? model)
        {
            var key = $"{tenantId}|{manufacturer ?? ""}|{model ?? ""}";
            var now = DateTime.UtcNow;

            var stored = _lastNotified.AddOrUpdate(
                key,
                addValueFactory: _ => now,
                updateValueFactory: (_, existing) =>
                    (now - existing) >= ThrottleWindow ? now : existing);

            return stored == now;
        }
    }
}
