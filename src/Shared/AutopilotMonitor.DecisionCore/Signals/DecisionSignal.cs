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
            IReadOnlyDictionary<string, string>? payload = null,
            object? typedPayload = null)
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
            TypedPayload = typedPayload;
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

        /// <summary>
        /// Optional sidecar for structured data that does not fit the string-only
        /// <see cref="Payload"/> contract. Flows through the bus object-identity-preserving —
        /// the reducer forwards it into a <see cref="Engine.DecisionEffect.TypedPayload"/> and
        /// the EffectRunner hands it to the receiving consumer untouched.
        /// <para>
        /// Single-rail refactor (plan §1.3): informational events use this to carry their
        /// <see cref="Shared.Models.EnrollmentEvent.Data"/> dictionary — with nested
        /// <c>Dictionary</c> / <c>List</c> structures — from the collector through the engine
        /// to <c>EventTimelineEmitter</c> without any intermediate serialization. Persistence
        /// writes a JSON representation once at the disk boundary (<c>SignalSerializer</c>);
        /// on replay the payload is restored as a <c>JObject</c> / <c>JArray</c> token that
        /// re-serializes to the same wire shape.
        /// </para>
        /// <para>
        /// Decision-relevant payload still belongs in <see cref="Payload"/> — the reducer reads
        /// it to mutate state. <see cref="TypedPayload"/> is invisible to reducer logic by
        /// convention; promoting an informational event to a real <see cref="DecisionSignalKind"/>
        /// later means having the new reducer case read both fields.
        /// </para>
        /// </summary>
        public object? TypedPayload { get; }
    }
}
