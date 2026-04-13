using System;
using System.Collections.Concurrent;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// In-memory throttle to prevent flooding admins with SLA breach notifications.
    /// Cooldown period of 4 hours per tenant per notification type.
    /// </summary>
    public class SlaNotificationThrottleService
    {
        private static readonly TimeSpan CooldownPeriod = TimeSpan.FromHours(4);
        private readonly ConcurrentDictionary<string, DateTime> _lastNotified = new();

        /// <summary>
        /// Returns true if a notification should be sent (enough time has passed since the last one).
        /// Atomically updates the last-notified timestamp if the cooldown has elapsed.
        /// </summary>
        /// <param name="tenantId">Tenant identifier.</param>
        /// <param name="notificationType">Notification type key (e.g. "sla_breach", "consecutive_failures").</param>
        public bool ShouldNotify(string tenantId, string notificationType)
        {
            var key = $"{tenantId}|{notificationType}";
            var now = DateTime.UtcNow;

            var stored = _lastNotified.AddOrUpdate(
                key,
                _ => now,
                (_, existing) => (now - existing) >= CooldownPeriod ? now : existing);

            return stored == now;
        }

        /// <summary>
        /// Resets the throttle for a specific tenant and notification type (for testing).
        /// </summary>
        internal void Reset(string tenantId, string notificationType)
        {
            var key = $"{tenantId}|{notificationType}";
            _lastNotified.TryRemove(key, out _);
        }
    }
}
