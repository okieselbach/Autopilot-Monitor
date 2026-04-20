#nullable enable
using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Factory-Seam für Collector-Hosts. Plan §4.x M4.4.5.c.
    /// <para>
    /// Der <see cref="EnrollmentOrchestrator"/> delegiert an diese Factory, weil die konkreten
    /// Collector-Ctoren heavy Production-Deps (Event-Log-Watcher, WMI, Registry) haben — Tests
    /// liefern eine Fake-Factory, Prod liefert eine Default-Implementation, die HelloTracker,
    /// ShellCoreTracker, ProvisioningStatusTracker, StallProbeCollector und
    /// ModernDeploymentTracker baut und deren <c>onEventCollected</c>-Callbacks an den
    /// übergebenen <paramref name="onEnrollmentEvent"/> bindet.
    /// </para>
    /// <para>
    /// <b>ModernDeploymentTracker-Spezifikum</b>: der einzige Kollektor ohne SignalAdapter —
    /// bridged nur Events, keine DecisionSignals. Default-Factory-Impls dürfen ihn aufbauen,
    /// Tests brauchen ihn nicht zwingend.
    /// </para>
    /// </summary>
    public interface IComponentFactory
    {
        /// <summary>
        /// Baut alle Collector-Hosts und bindet deren Event-Emission an
        /// <paramref name="onEnrollmentEvent"/> (typischerweise
        /// <see cref="Telemetry.Events.TelemetryEventEmitter.Emit"/>).
        /// Exceptions im Callback darf die Factory NICHT weiterreichen — der Orchestrator
        /// wrappt das mit einem Exception-Swallow, damit ein Collector-Thread nicht stirbt.
        /// <para>
        /// <paramref name="whiteGloveSealingPatternIds"/> wird an den <c>ImeLogTrackerAdapter</c>
        /// durchgereicht (Sealing-Emission fire-once nur für diese Pattern-IDs). Leer / null
        /// = Feature off, M3-kompatibel. Plan §4.x M4.4.5.e.
        /// </para>
        /// </summary>
        IReadOnlyList<ICollectorHost> CreateCollectorHosts(
            string sessionId,
            string tenantId,
            AgentLogger logger,
            Action<EnrollmentEvent> onEnrollmentEvent,
            IReadOnlyCollection<string> whiteGloveSealingPatternIds);
    }
}
