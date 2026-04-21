using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Backend storage record for a single DecisionSignal (Plan §M5). Flat shape projected
    /// from the agent's DecisionSignal so the Backend doesn't need to reference DecisionCore
    /// just to persist and serve it back out.
    /// <para>
    /// <b>Keys</b> (authoritative, supplied by the agent for idempotent upsert via
    /// <c>(PartitionKey, RowKey)</c>):
    /// PK = <c>{TenantId}_{SessionId}</c>, RK = <c>{SessionSignalOrdinal:D19}</c>.
    /// </para>
    /// <para>
    /// <b>Fidelity</b>: <see cref="PayloadJson"/> carries the complete agent-serialized
    /// DecisionSignal (including Evidence + Payload dictionary). Typed columns exist only
    /// for query/projection; replay is driven off the JSON blob.
    /// </para>
    /// </summary>
    public sealed class SignalRecord
    {
        public string TenantId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;

        /// <summary>Monotonic per SignalLog. Drives table RowKey ordering.</summary>
        public long SessionSignalOrdinal { get; set; }

        /// <summary>Session-wide monotonic across Event + Signal + Transition. Inspector correlation only.</summary>
        public long SessionTraceOrdinal { get; set; }

        /// <summary>DecisionSignalKind enum name — stored as string for forward-compat.</summary>
        public string Kind { get; set; } = string.Empty;

        public int KindSchemaVersion { get; set; }

        public DateTime OccurredAtUtc { get; set; }

        public string SourceOrigin { get; set; } = string.Empty;

        /// <summary>Agent-serialized DecisionSignal JSON (Evidence + Payload included).</summary>
        public string PayloadJson { get; set; } = string.Empty;
    }
}
