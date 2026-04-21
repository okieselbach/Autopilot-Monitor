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
        /// <summary>Post ein synthetisches Signal an den Ingress.</summary>
        void Post(
            DecisionSignalKind kind,
            DateTime occurredAtUtc,
            string sourceOrigin,
            Evidence evidence,
            IReadOnlyDictionary<string, string>? payload = null,
            int kindSchemaVersion = 1);
    }
}
