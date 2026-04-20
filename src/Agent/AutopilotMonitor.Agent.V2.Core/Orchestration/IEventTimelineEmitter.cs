#nullable enable
using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Abstraktion für <see cref="AutopilotMonitor.DecisionCore.Engine.DecisionEffectKind.EmitEventTimelineEntry"/>-Effekte.
    /// Plan §2.5 / §2.13.
    /// <para>
    /// Die konkrete Implementierung (M4.4.1
    /// <see cref="Telemetry.Events.EventTimelineEmitter"/>) baut aus Effect-Parametern + aktuellem
    /// <see cref="DecisionState"/> einen <c>TelemetryItemDraft</c> mit <c>Kind=Event</c>,
    /// vergibt das Events-Table-Schema (EventType, Sequence, DataJson, …) und enqueued via
    /// <see cref="Transport.Telemetry.ITelemetryTransport"/>.
    /// </para>
    /// </summary>
    public interface IEventTimelineEmitter
    {
        /// <summary>
        /// Emittiert einen Event-Timeline-Eintrag. <paramref name="parameters"/> enthält
        /// typischerweise <c>eventType</c> und optional <c>reason</c> (vom Reducer gesetzt).
        /// <paramref name="occurredAtUtc"/> ist der deterministische Schritt-Zeitstempel
        /// (<c>DecisionSignal.OccurredAtUtc</c> des auslösenden Signals) — nicht
        /// <c>clock.UtcNow</c>. Damit bleibt die Event-Timeline replay-deterministisch.
        /// </summary>
        void Emit(
            IReadOnlyDictionary<string, string>? parameters,
            DecisionState currentState,
            DateTime occurredAtUtc);
    }
}
