#nullable enable
using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Telemetry.Events
{
    /// <summary>
    /// Concrete <see cref="IEventTimelineEmitter"/> implementation. Plan §2.5 / §2.13 / L.10.
    /// <para>
    /// Translates <c>EmitEventTimelineEntry</c> effects (parameter dict with <c>eventType</c>,
    /// optional <c>reason</c>, optional <c>phase</c>, plus scenario-specific context keys) into
    /// an <see cref="EnrollmentEvent"/> and delegates to <see cref="TelemetryEventEmitter"/>.
    /// </para>
    /// <para>
    /// <b>Phase invariant</b> (<c>feedback_phase_strategy</c>, cf. <see cref="EnrollmentPhase"/>
    /// XML doc): <see cref="EnrollmentEvent.Phase"/> defaults to <see cref="EnrollmentPhase.Unknown"/>
    /// — the UI timeline sorts these events chronologically into the currently active phase.
    /// Only explicit phase-declaration events (e.g. <c>agent_started</c>, <c>esp_phase_changed</c>)
    /// may declare a phase by setting a <see cref="PhaseParamKey"/> parameter with the enum name
    /// as a string (e.g. <c>"DeviceSetup"</c>) on the <c>EmitEventTimelineEntry</c> effect.
    /// Unknown / unparsable values fall back to <c>Unknown</c>; this emitter path must never
    /// throw on a malformed phase value.
    /// </para>
    /// </summary>
    public sealed class EventTimelineEmitter : IEventTimelineEmitter
    {
        internal const string SourceId = "decision_engine";
        internal const string EventTypeParamKey = "eventType";

        /// <summary>
        /// Parameter key for the optional phase-declaration override. The value must be the
        /// exact <see cref="EnrollmentPhase"/> enum name (Ordinal match, case-sensitive).
        /// Missing key or unparsable value → <see cref="EnrollmentPhase.Unknown"/>.
        /// </summary>
        internal const string PhaseParamKey = "phase";

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
                Phase = ParsePhase(parameters),
                Message = BuildMessage(eventType, parameters),
                Timestamp = DateTime.SpecifyKind(occurredAtUtc, DateTimeKind.Utc),
                ImmediateUpload = true,     // terminal + classification events are always immediate
                Data = BuildDataDict(parameters),
            };

            _emitter.Emit(evt);
        }

        /// <summary>
        /// Reads the optional <see cref="PhaseParamKey"/> parameter and parses it into an
        /// <see cref="EnrollmentPhase"/>. Missing / empty / unparsable value → <c>Unknown</c>.
        /// Deterministic, never throws — the Unknown fallback is the safe default invariant
        /// described in the class-level comment.
        /// </summary>
        private static EnrollmentPhase ParsePhase(IReadOnlyDictionary<string, string> parameters)
        {
            if (!parameters.TryGetValue(PhaseParamKey, out var raw) || string.IsNullOrEmpty(raw))
            {
                return EnrollmentPhase.Unknown;
            }

            return Enum.TryParse<EnrollmentPhase>(raw, ignoreCase: false, out var parsed)
                ? parsed
                : EnrollmentPhase.Unknown;
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
                // Top-level EnrollmentEvent fields (EventType, Phase) are not duplicated in Data.
                if (kv.Key == EventTypeParamKey) continue;
                if (kv.Key == PhaseParamKey) continue;
                data[kv.Key] = kv.Value;
            }
            return data;
        }
    }
}
