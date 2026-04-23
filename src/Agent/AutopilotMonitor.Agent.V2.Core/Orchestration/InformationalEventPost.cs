#nullable enable
using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Thin helper over <see cref="ISignalIngressSink"/> for posting
    /// <see cref="DecisionSignalKind.InformationalEvent"/> signals without the caller having
    /// to assemble the payload dictionary or the <see cref="Evidence"/> record.
    /// Plan §1.3 / §4.3.
    /// <para>
    /// The single-rail refactor forbids collectors / lifecycle sources from calling
    /// <c>TelemetryEventEmitter.Emit</c> directly. Instead, they construct a payload and call
    /// <see cref="Emit"/>: the reducer's InformationalEvent pass-through case converts that
    /// into an <see cref="DecisionEffectKind.EmitEventTimelineEntry"/> effect whose parameters
    /// are read verbatim by <see cref="Telemetry.Events.EventTimelineEmitter"/>. One ordered
    /// pipe, replay-deterministic, same UI wire shape.
    /// </para>
    /// <para>
    /// <b>Timestamp semantics</b>: <paramref name="occurredAtUtc"/> is the event's wall-clock
    /// moment — falls back to <see cref="IClock.UtcNow"/> when omitted. This value flows through
    /// as the signal's <c>OccurredAtUtc</c> AND the reducer's step-timestamp, so both the
    /// <c>Events</c> and <c>DecisionTransitions</c> tables agree on when the fact occurred.
    /// </para>
    /// <para>
    /// Promotion path: when an event later needs to influence a decision, swap the sender to
    /// a dedicated <see cref="DecisionSignalKind"/> and add a state-mutating reducer case. The
    /// on-wire shape stays identical because the reducer still produces the same
    /// <c>EmitEventTimelineEntry</c> effect parameters.
    /// </para>
    /// </summary>
    public sealed class InformationalEventPost
    {
        // Effect-parameter key for the optional phase override. Must match the const value in
        // Telemetry.Events.EventTimelineEmitter.PhaseParamKey (internal there) so the emitter
        // interprets a phase set here as an EnrollmentPhase enum name.
        private const string PhaseParamKey = "phase";

        private readonly ISignalIngressSink _ingress;
        private readonly IClock _clock;

        public InformationalEventPost(ISignalIngressSink ingress, IClock clock)
        {
            _ingress = ingress ?? throw new ArgumentNullException(nameof(ingress));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        /// <summary>
        /// Post an informational event to the decision-engine ingress.
        /// </summary>
        /// <param name="eventType">Backend event-type string (becomes <see cref="EnrollmentEvent.EventType"/>). Mandatory.</param>
        /// <param name="source">Originating component label (becomes <see cref="EnrollmentEvent.Source"/>). Mandatory.</param>
        /// <param name="message">Optional human-readable message. When omitted, the emitter falls back to <c>{eventType}: {reason}</c> / <c>eventType</c>.</param>
        /// <param name="severity">Optional severity; default falls through to suffix-derived <see cref="EventSeverity"/> in the emitter.</param>
        /// <param name="immediateUpload">When <c>true</c>, the event bypasses the debounce timer and triggers an immediate transport flush.</param>
        /// <param name="phase">Optional phase declaration (only for phase-declaration events like <c>agent_started</c>, <c>esp_phase_changed</c>). Default emits <see cref="EnrollmentPhase.Unknown"/>.</param>
        /// <param name="data">Optional data fields; merged into <see cref="EnrollmentEvent.Data"/>. Reserved top-level keys (eventType, source, severity, message, immediateUpload, phase) cannot be overridden via this dictionary.</param>
        /// <param name="occurredAtUtc">Event time; defaults to <see cref="IClock.UtcNow"/>.</param>
        /// <param name="sourceOrigin">Identity of the posting component for the <see cref="Evidence"/> record; defaults to <paramref name="source"/>.</param>
        /// <param name="evidenceSummary">Free-text evidence summary; defaults to <paramref name="message"/> or <paramref name="eventType"/>.</param>
        public void Emit(
            string eventType,
            string source,
            string? message = null,
            EventSeverity? severity = null,
            bool immediateUpload = false,
            EnrollmentPhase? phase = null,
            IReadOnlyDictionary<string, string>? data = null,
            DateTime? occurredAtUtc = null,
            string? sourceOrigin = null,
            string? evidenceSummary = null)
        {
            if (string.IsNullOrEmpty(eventType))
                throw new ArgumentException("eventType is mandatory.", nameof(eventType));
            if (string.IsNullOrEmpty(source))
                throw new ArgumentException("source is mandatory.", nameof(source));

            var payload = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SignalPayloadKeys.EventType] = eventType,
                [SignalPayloadKeys.Source] = source,
            };

            if (!string.IsNullOrEmpty(message))
                payload[SignalPayloadKeys.Message] = message!;
            if (severity.HasValue)
                payload[SignalPayloadKeys.Severity] = severity.Value.ToString();
            if (immediateUpload)
                payload[SignalPayloadKeys.ImmediateUpload] = "true";
            if (phase.HasValue)
                payload[PhaseParamKey] = phase.Value.ToString();

            if (data != null)
            {
                foreach (var kv in data)
                {
                    // Caller-provided data never overrides the reserved top-level keys we just set,
                    // so a data entry named "source" cannot accidentally rewrite EnrollmentEvent.Source.
                    if (payload.ContainsKey(kv.Key)) continue;
                    payload[kv.Key] = kv.Value;
                }
            }

            var stamp = occurredAtUtc ?? _clock.UtcNow;
            var evidence = new Evidence(
                kind: EvidenceKind.Raw,
                identifier: $"informational_event:{eventType}",
                summary: !string.IsNullOrEmpty(evidenceSummary)
                    ? evidenceSummary!
                    : !string.IsNullOrEmpty(message) ? message! : eventType);

            _ingress.Post(
                kind: DecisionSignalKind.InformationalEvent,
                occurredAtUtc: stamp,
                sourceOrigin: !string.IsNullOrEmpty(sourceOrigin) ? sourceOrigin! : source,
                evidence: evidence,
                payload: payload);
        }
    }
}
