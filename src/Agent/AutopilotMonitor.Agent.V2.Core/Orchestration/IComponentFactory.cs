#nullable enable
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.DecisionCore.Engine;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Factory-Seam für Collector-Hosts. Plan §4.x M4.4.5.c / §5.10 (single-rail enforcement).
    /// <para>
    /// Der <see cref="EnrollmentOrchestrator"/> delegiert an diese Factory, weil die konkreten
    /// Collector-Ctoren heavy Production-Deps (Event-Log-Watcher, WMI, Registry) haben — Tests
    /// liefern eine Fake-Factory, Prod liefert eine Default-Implementation, die die Tracker
    /// + Hosts baut. Nach PR #10 gibt es keinen <c>Action&lt;EnrollmentEvent&gt;</c>-Parameter
    /// mehr — alle Collectors posten via <paramref name="ingress"/> (Signal-Rail) statt
    /// direkt an den <see cref="Telemetry.Events.TelemetryEventEmitter"/>.
    /// </para>
    /// <para>
    /// <paramref name="whiteGloveSealingPatternIds"/> wird an den <c>ImeLogTrackerAdapter</c>
    /// durchgereicht (Sealing-Emission fire-once nur für diese Pattern-IDs). Leer / null
    /// = Feature off, M3-kompatibel. Plan §4.x M4.4.5.e.
    /// </para>
    /// </summary>
    public interface IComponentFactory
    {
        /// <summary>
        /// Baut alle Collector-Hosts. Jeder Host ist selbst dafür zuständig, seine
        /// <c>InformationalEventPost</c> aus (ingress, clock) zu konstruieren und Collector-Events
        /// als <c>InformationalEvent</c>-Signals über <paramref name="ingress"/> zu posten.
        /// </summary>
        IReadOnlyList<ICollectorHost> CreateCollectorHosts(
            string sessionId,
            string tenantId,
            AgentLogger logger,
            IReadOnlyCollection<string> whiteGloveSealingPatternIds,
            ISignalIngressSink ingress,
            IClock clock);
    }
}
