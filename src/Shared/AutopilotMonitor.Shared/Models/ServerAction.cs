using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Server-issued action that the agent should execute.
    /// Delivered piggy-backed on ingest responses — no extra polling required.
    ///
    /// Actions are queued on the session row (Sessions.PendingActionsJson) by server-side writers
    /// (admin endpoints, rule engine, maintenance) and picked up on the agent's next ingest call.
    /// Delivery is best-effort at-least-once: the agent must be idempotent for its intended operation.
    /// </summary>
    public class ServerAction
    {
        /// <summary>
        /// Action type — one of <see cref="ServerActionTypes"/>. Unknown types are logged and skipped
        /// by the agent so rolling out a new type doesn't require a synchronous agent update.
        /// </summary>
        public string Type { get; set; } = string.Empty;

        /// <summary>
        /// Human-readable reason for this action. Surfaced in logs and agent telemetry.
        /// Do not include secrets — values flow through Application Insights.
        /// </summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>
        /// Optional rule that triggered this action. Set when queued from the RuleEngine,
        /// null for admin/manual triggers.
        /// </summary>
        public string? RuleId { get; set; }

        /// <summary>
        /// Free-form string parameters for the action. Each action type defines its own keys.
        /// Using string values (not nested objects) keeps the on-wire shape flat and predictable
        /// and sidesteps JSON deserialization quirks in the net48 agent.
        ///
        /// Conventions:
        ///   terminate_session:
        ///     gracePeriodSeconds    — (optional) seconds the agent keeps collecting before shutdown; default 30
        ///     uploadDiagnostics     — (optional) "true"/"false"; attempt a best-effort diagnostics upload pre-shutdown
        ///   rotate_config:
        ///     (no parameters)
        ///   request_diagnostics:
        ///     (no parameters)
        /// </summary>
        public Dictionary<string, string>? Params { get; set; }

        /// <summary>
        /// When this action was queued. Used for TTL / staleness detection and telemetry.
        /// </summary>
        public DateTime QueuedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Canonical action type strings. Kept as constants so typos fail at compile time.
    /// </summary>
    public static class ServerActionTypes
    {
        /// <summary>Agent should stop collecting, best-effort upload, then exit.</summary>
        public const string TerminateSession = "terminate_session";

        /// <summary>Agent should re-fetch its config from /api/agent/config.</summary>
        public const string RotateConfig = "rotate_config";

        /// <summary>Agent should trigger a diagnostics package upload.</summary>
        public const string RequestDiagnostics = "request_diagnostics";
    }
}
