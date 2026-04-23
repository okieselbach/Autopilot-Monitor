#nullable enable
using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Signals;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Senke für synthetische Signals, die der EffectRunner in den Ingress zurückspeist
    /// (z.B. <c>ClassifierVerdictIssued</c>). Plan §2.1a / §2.4.
    /// <para>
    /// Ingress übernimmt Ordinal-Vergabe, SignalLog-Append und die anschließende Reducer-
    /// Reingestion in derselben Single-Writer-Serialisierung wie Collector-Signals.
    /// EffectRunner kennt die Ordinal-Logik nicht.
    /// </para>
    /// <para>
    /// M4.2 liefert nur die Abstraktion + Test-Fake; die reale Implementierung wird in M4.4
    /// mit dem Enrollment-Orchestrator gewired.
    /// </para>
    /// </summary>
    public interface ISignalIngressSink
    {
        /// <summary>
        /// Post ein synthetisches Signal an den Ingress.
        /// <paramref name="typedPayload"/> ist ein optionaler Sidecar für strukturierte Daten,
        /// die nicht in den string-only <paramref name="payload"/>-Contract passen. Plan §1.3 —
        /// z.B. <c>EnrollmentEvent.Data</c> mit nested Dict/List wird so untouched zum
        /// <c>EventTimelineEmitter</c> durchgereicht, ohne intermediäre Serialisierung.
        /// </summary>
        void Post(
            DecisionSignalKind kind,
            DateTime occurredAtUtc,
            string sourceOrigin,
            Evidence evidence,
            IReadOnlyDictionary<string, string>? payload = null,
            int kindSchemaVersion = 1,
            object? typedPayload = null);
    }
}
