#nullable enable

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Session-weiter, monotoner Trace-Counter für <c>SessionTraceOrdinal</c>. Plan §2.2.
    /// <para>
    /// Wird in M4.4.0 nur von <see cref="SignalIngress"/> konsumiert (Signals); in M4.4.1
    /// kommt der zweite Konsument dazu (Events + Transitions via
    /// <see cref="Transport.Telemetry.ITelemetryTransport"/>). Der Provider ist dann die
    /// zentrale Instanz, die das „alle drei Typen"-Versprechen aus §2.2 realisiert.
    /// </para>
    /// <para>
    /// Thread-safe. Impl vergibt monotone Werte via <c>Interlocked</c>.
    /// </para>
    /// </summary>
    public interface ISessionTraceOrdinalProvider
    {
        /// <summary>Vergibt den nächsten Ordinal. Strikt monoton steigend pro Session.</summary>
        long Next();

        /// <summary>Zuletzt vergebener Wert; <c>-1</c> wenn noch keiner vergeben ist.</summary>
        long LastAssigned { get; }
    }
}
