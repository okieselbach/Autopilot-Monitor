#nullable enable
using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Telemetry.Events
{
    /// <summary>
    /// Konkrete <see cref="IEventTimelineEmitter"/>-Implementierung. Plan §2.5 / §2.13 / L.10.
    /// <para>
    /// Verwandelt <c>EmitEventTimelineEntry</c>-Effekte (Parameter-Dict mit <c>eventType</c>,
    /// optional <c>reason</c> + scenario-spezifische Kontext-Keys) in einen
    /// <see cref="EnrollmentEvent"/> und delegiert an <see cref="TelemetryEventEmitter"/>.
    /// </para>
    /// <para>
    /// <b>Phase-Invariante</b> (feedback_phase_strategy): Terminal-Events deklarieren KEINEN
    /// Phase-Übergang — <see cref="EnrollmentEvent.Phase"/> bleibt <c>Unknown</c>, damit die
    /// UI-Timeline die Events chronologisch in die aktive Phase einsortiert.
    /// </para>
    /// </summary>
    public sealed class EventTimelineEmitter : IEventTimelineEmitter
    {
        internal const string SourceId = "decision_engine";
        internal const string EventTypeParamKey = "eventType";

        private readonly TelemetryEventEmitter _emitter;

        public EventTimelineEmitter(TelemetryEventEmitter emitter)
        {
            _emitter = emitter ?? throw new ArgumentNullException(nameof(emitter));
        }

        public void Emit(
            IReadOnlyDictionary<string, string>? parameters,
            DecisionState currentState,
            DateTime occurredAtUtc)
        {
            if (currentState == null) throw new ArgumentNullException(nameof(currentState));

            if (parameters == null || !parameters.TryGetValue(EventTypeParamKey, out var eventType) || string.IsNullOrEmpty(eventType))
            {
                throw new ArgumentException(
                    "EmitEventTimelineEntry effect must provide a non-empty 'eventType' parameter.",
                    nameof(parameters));
            }

            var evt = new EnrollmentEvent
            {
                SessionId = currentState.SessionId,
                TenantId = currentState.TenantId,
                EventType = eventType,
                Severity = DeriveSeverity(eventType),
                Source = SourceId,
                Phase = EnrollmentPhase.Unknown,
                Message = BuildMessage(eventType, parameters),
                Timestamp = DateTime.SpecifyKind(occurredAtUtc, DateTimeKind.Utc),
                ImmediateUpload = true,     // terminal + classification events are always immediate
                Data = BuildDataDict(parameters),
            };

            _emitter.Emit(evt);
        }

        private static EventSeverity DeriveSeverity(string eventType)
        {
            if (eventType.EndsWith("_failed", StringComparison.Ordinal)) return EventSeverity.Error;
            if (eventType.EndsWith("_aborted", StringComparison.Ordinal)) return EventSeverity.Warning;
            return EventSeverity.Info;
        }

        private static string BuildMessage(string eventType, IReadOnlyDictionary<string, string> parameters)
        {
            if (parameters.TryGetValue("reason", out var reason) && !string.IsNullOrEmpty(reason))
            {
                return $"{eventType}: {reason}";
            }
            return eventType;
        }

        private static Dictionary<string, object> BuildDataDict(IReadOnlyDictionary<string, string> parameters)
        {
            var data = new Dictionary<string, object>(parameters.Count, StringComparer.Ordinal);
            foreach (var kv in parameters)
            {
                // eventType is the top-level EnrollmentEvent.EventType, not a data key — don't duplicate.
                if (kv.Key == EventTypeParamKey) continue;
                data[kv.Key] = kv.Value;
            }
            return data;
        }
    }
}
