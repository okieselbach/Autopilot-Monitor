#nullable enable
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace AutopilotMonitor.Agent.V2.Core.Transport.Telemetry
{
    /// <summary>
    /// Typ eines <see cref="TelemetryItem"/>. Plan §2.7a / L.10.
    /// <para>
    /// Drei getrennte Backend-Tabellen:
    /// <see cref="Event"/> → <c>Events</c>,
    /// <see cref="Signal"/> → <c>Signals</c>,
    /// <see cref="DecisionTransition"/> → <c>DecisionTransitions</c>.
    /// Der generische Transport routet auf Backend-Seite nach <c>Kind</c>.
    /// </para>
    /// <para>
    /// String-serialisiert via <see cref="StringEnumConverter"/> — Backend-Routing darf
    /// nicht von Integer-Ordering abhängen, das bei zukünftigen Erweiterungen bricht
    /// (L.14 forward-compat).
    /// </para>
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum TelemetryItemKind
    {
        Event = 0,
        Signal,
        DecisionTransition,
    }
}
