#nullable enable
using System;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Beobachter für Back-Pressure am <see cref="SignalIngress"/>-Channel. Plan §2.1a.
    /// <para>
    /// SignalIngress ruft diesen Observer auf, wenn ein <c>Post</c>-Call blockieren musste,
    /// weil der Channel voll war. Die Throttle-Logik (max. 1×/min/origin) passiert in
    /// <see cref="SignalIngress"/>; der Observer bekommt nur die bereits gefilterten
    /// Ereignisse.
    /// </para>
    /// <para>
    /// Konkrete Impl in M4.4.1: routet als <c>agent_trace</c>-Event mit
    /// <c>EventType="ingress_backpressure"</c> über den Events-Bridge-Pfad
    /// (<see cref="Transport.Telemetry.ITelemetryTransport.Enqueue"/> Kind=Event), nicht
    /// durch den Ingress-Channel selbst (würde deadlocken — §2.1a).
    /// </para>
    /// </summary>
    public interface IBackPressureObserver
    {
        /// <summary>
        /// Meldet ein Back-Pressure-Ereignis.
        /// </summary>
        /// <param name="origin">Collector-Origin des blockierten Producers (<c>DecisionSignal.SourceOrigin</c>).</param>
        /// <param name="channelCapacity">Konfigurierte Channel-Kapazität.</param>
        /// <param name="queueLength">Warteschlangen-Länge beim Detektionspunkt.</param>
        /// <param name="blockDuration">Wie lange der Producer tatsächlich geblockt hat.</param>
        void OnBackPressure(string origin, int channelCapacity, int queueLength, TimeSpan blockDuration);
    }
}
