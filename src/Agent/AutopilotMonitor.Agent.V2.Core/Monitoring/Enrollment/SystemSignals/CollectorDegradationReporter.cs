using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// Emits a single <c>collector_degraded</c> Warning event when a collector/watcher fails to
    /// arm or operate. Review MON-D1: kernel event-log/registry watcher failures were previously
    /// local-log only, so a dead Shell-Core / ModernDeployment / Hello watcher looked identical on
    /// the backend to a genuine no-signal enrollment until the 5 h maintenance sweep. This surfaces
    /// the failure as telemetry without violating the no-heartbeat rule — it is one-shot,
    /// state-change-only (fired on the failure, not periodically).
    /// </summary>
    internal static class CollectorDegradationReporter
    {
        /// <summary>
        /// Best-effort emit of a <c>collector_degraded</c> Warning. Never throws — it runs from
        /// failure paths where an additional exception would be worse than a missing event.
        /// </summary>
        /// <param name="post">Telemetry post target (no-op if null).</param>
        /// <param name="sessionId">Session correlation id.</param>
        /// <param name="tenantId">Tenant correlation id.</param>
        /// <param name="collectorName">The collector/watcher that degraded (also the event Source).</param>
        /// <param name="reason">Short machine-readable reason (e.g. <c>watcher_arm_failed</c>).</param>
        /// <param name="ex">Optional underlying exception — its type + message are attached.</param>
        public static void Report(
            InformationalEventPost post,
            string sessionId,
            string tenantId,
            string collectorName,
            string reason,
            Exception ex = null)
        {
            if (post == null) return;

            try
            {
                var data = new Dictionary<string, object>
                {
                    { "collector", collectorName },
                    { "reason", reason },
                };
                if (ex != null)
                {
                    data["error"] = ex.Message;
                    data["errorType"] = ex.GetType().Name;
                }

                var message = ex != null
                    ? $"Collector '{collectorName}' degraded: {reason} ({ex.GetType().Name}: {ex.Message})"
                    : $"Collector '{collectorName}' degraded: {reason}";

                post.Emit(new EnrollmentEvent
                {
                    SessionId = sessionId,
                    TenantId = tenantId,
                    EventType = Constants.EventTypes.CollectorDegraded,
                    Severity = EventSeverity.Warning,
                    Source = collectorName,
                    Phase = EnrollmentPhase.Unknown,
                    Message = message,
                    Data = data,
                });
            }
            catch
            {
                // Best-effort observability — never throw from a failure path.
            }
        }
    }
}
