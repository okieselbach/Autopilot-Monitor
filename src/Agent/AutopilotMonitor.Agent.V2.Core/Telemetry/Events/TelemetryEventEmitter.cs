#nullable enable
using System;
using AutopilotMonitor.Agent.V2.Core.Transport.Telemetry;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.V2.Core.Telemetry.Events
{
    /// <summary>
    /// Zentrale Senke für alle <see cref="EnrollmentEvent"/>-Emissionen im V2-Agent.
    /// Plan §2.7a / L.10.
    /// <para>
    /// Drei Aufrufer: <see cref="EventTimelineEmitter"/> (Reducer-Effekte),
    /// Collector-Callbacks (via Orchestrator-Wiring in M4.4.5) und
    /// <see cref="BackPressureEventObserver"/>. Alle nutzen denselben Pfad:
    /// Sequence-Vergabe → Wire-Format-Serialisierung (Newtonsoft, Legacy-kompatibel) →
    /// <see cref="TelemetryItemDraft"/> Kind=Event → <see cref="ITelemetryTransport.Enqueue"/>.
    /// </para>
    /// <para>
    /// <b>Mutation</b>: setzt <see cref="EnrollmentEvent.Sequence"/>, <see cref="EnrollmentEvent.RowKey"/>,
    /// <see cref="EnrollmentEvent.SessionId"/>, <see cref="EnrollmentEvent.TenantId"/> falls leer.
    /// Caller darf das Event nach <see cref="Emit"/> nicht weiter verwenden.
    /// </para>
    /// </summary>
    public sealed class TelemetryEventEmitter
    {
        private readonly ITelemetryTransport _transport;
        private readonly EventSequenceCounter _sequenceCounter;
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly string _partitionKey;

        public TelemetryEventEmitter(
            ITelemetryTransport transport,
            EventSequenceCounter sequenceCounter,
            string sessionId,
            string tenantId)
        {
            if (string.IsNullOrEmpty(sessionId)) throw new ArgumentException("SessionId is mandatory.", nameof(sessionId));
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentException("TenantId is mandatory.", nameof(tenantId));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _sequenceCounter = sequenceCounter ?? throw new ArgumentNullException(nameof(sequenceCounter));
            _sessionId = sessionId;
            _tenantId = tenantId;
            _partitionKey = $"{tenantId}_{sessionId}";
        }

        /// <summary>
        /// Weist Sequence zu, serialisiert im Legacy-Wire-Format und enqueued via Transport.
        /// Thread-safe (EventSequenceCounter + ITelemetryTransport.Enqueue beide unter Lock).
        /// </summary>
        public TelemetryItem Emit(EnrollmentEvent evt)
        {
            if (evt == null) throw new ArgumentNullException(nameof(evt));
            if (string.IsNullOrEmpty(evt.EventType))
            {
                throw new ArgumentException("EnrollmentEvent.EventType is mandatory.", nameof(evt));
            }

            evt.Sequence = _sequenceCounter.Next();
            if (string.IsNullOrEmpty(evt.SessionId)) evt.SessionId = _sessionId;
            if (string.IsNullOrEmpty(evt.TenantId)) evt.TenantId = _tenantId;

            var rowKey = $"{evt.Timestamp:yyyyMMddHHmmssfff}_{evt.Sequence:D10}";
            evt.RowKey = rowKey;

            var payloadJson = JsonConvert.SerializeObject(evt, Formatting.None);

            var draft = new TelemetryItemDraft(
                kind: TelemetryItemKind.Event,
                partitionKey: _partitionKey,
                rowKey: rowKey,
                payloadJson: payloadJson,
                isSessionScoped: true,
                requiresImmediateFlush: evt.ImmediateUpload);

            return _transport.Enqueue(draft);
        }
    }
}
