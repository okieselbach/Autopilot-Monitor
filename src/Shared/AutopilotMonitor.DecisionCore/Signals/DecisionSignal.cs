using System;
using System.Collections.Generic;

namespace AutopilotMonitor.DecisionCore.Signals
{
    /// <summary>
    /// Immutable input unit to the Decision Engine. Plan §2.2.
    /// <para>
    /// The SignalLog is append-only and is the single source of input truth (plan §2.7c L.1).
    /// Ordinals are assigned exclusively by SignalIngress (single-writer serialization, §2.1a)
    /// — never by collectors or adapters directly.
    /// </para>
    /// </summary>
    public sealed class DecisionSignal
    {
        public DecisionSignal(
            long sessionSignalOrdinal,
            long sessionTraceOrdinal,
            DecisionSignalKind kind,
            int kindSchemaVersion,
            DateTime occurredAtUtc,
            string sourceOrigin,
            Evidence evidence,
            IReadOnlyDictionary<string, string>? payload = null)
        {
            if (kindSchemaVersion < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(kindSchemaVersion),
                    "SchemaVersion must be >= 1.");
            }

            if (string.IsNullOrEmpty(sourceOrigin))
            {
                throw new ArgumentException(
                    "SourceOrigin is mandatory.",
                    nameof(sourceOrigin));
            }

            SessionSignalOrdinal = sessionSignalOrdinal;
            SessionTraceOrdinal = sessionTraceOrdinal;
            Kind = kind;
            KindSchemaVersion = kindSchemaVersion;
            OccurredAtUtc = occurredAtUtc;
            SourceOrigin = sourceOrigin;
            Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
            Payload = payload;
        }

        /// <summary>Monotonic per SignalLog; RowKey for Azure Signals table.</summary>
        public long SessionSignalOrdinal { get; }

        /// <summary>Session-wide monotonic across all telemetry types (Event + Signal + Transition). Inspector correlation only.</summary>
        public long SessionTraceOrdinal { get; }

        public DecisionSignalKind Kind { get; }

        /// <summary>Plan §2.2 L.6 — reducer dispatches on (Kind, KindSchemaVersion).</summary>
        public int KindSchemaVersion { get; }

        public DateTime OccurredAtUtc { get; }

        public string SourceOrigin { get; }

        public Evidence Evidence { get; }

        public IReadOnlyDictionary<string, string>? Payload { get; }
    }
}
