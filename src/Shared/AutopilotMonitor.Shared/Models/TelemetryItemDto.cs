using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Wire-format DTO for a single telemetry item sent by the V2 agent to
    /// <c>POST /api/agent/telemetry</c> (Plan §2.7a / §M5). The agent's in-memory class
    /// <c>AutopilotMonitor.Agent.V2.Core.Transport.Telemetry.TelemetryItem</c> is ctor-validated
    /// and immutable; this DTO mirrors the JSON shape so the backend can deserialise without
    /// depending on agent assemblies.
    /// <para>
    /// <b>Serialisation:</b> Newtonsoft.Json, PascalCase, <see cref="Kind"/> as string
    /// (<c>"Event"</c>/<c>"Signal"</c>/<c>"DecisionTransition"</c>) for forward-compat.
    /// </para>
    /// </summary>
    public sealed class TelemetryItemDto
    {
        /// <summary>
        /// Item type — routes to the destination table. Values: <c>Event</c>, <c>Signal</c>,
        /// <c>DecisionTransition</c>. Unknown values are rejected at parse time.
        /// </summary>
        public string Kind { get; set; } = string.Empty;

        /// <summary>Azure-Table PartitionKey, typically <c>{tenantId}_{sessionId}</c>.</summary>
        public string PartitionKey { get; set; } = string.Empty;

        public string RowKey { get; set; } = string.Empty;

        /// <summary>Transport-cursor — monotonic per session across all item kinds.</summary>
        public long TelemetryItemId { get; set; }

        /// <summary>Null for agent-global items (no session).</summary>
        public long? SessionTraceOrdinal { get; set; }

        /// <summary>Already-serialised JSON of the wrapped payload (EnrollmentEvent / DecisionSignal / DecisionTransition).</summary>
        public string PayloadJson { get; set; } = string.Empty;

        public bool RequiresImmediateFlush { get; set; }

        public DateTime EnqueuedAtUtc { get; set; }

        public int RetryCount { get; set; }
    }
}
