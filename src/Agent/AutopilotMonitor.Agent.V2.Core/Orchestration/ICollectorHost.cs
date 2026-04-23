#nullable enable
using System;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Lifecycle-Abstraktion für einen einzelnen Collector im V2-Runtime.
    /// Plan §2.1 / §4.x M4.4.5.c / §5.10 (single-rail enforcement).
    /// <para>
    /// Ein Host wrappt einen konkreten Collector (z.B. <c>HelloTracker</c>,
    /// <c>ShellCoreTracker</c>, <c>ModernDeploymentTracker</c>) und kapselt Start/Stop.
    /// Nach PR #10 posten Collectors ihre Events ausschließlich als
    /// <c>InformationalEvent</c>-Signals über den geteilten <see cref="ISignalIngressSink"/>,
    /// der über die <see cref="IComponentFactory"/> bei der Host-Erstellung injiziert wird.
    /// </para>
    /// <para>
    /// <b>SignalAdapter-Wiring</b>: separat — <see cref="IComponentFactory"/> kann die
    /// SignalAdapter-Instanzen mit-konstruieren und selbst an ihre Collector-Events
    /// subscriben. Der Orchestrator muss die Adapter-Seam nicht kennen.
    /// </para>
    /// </summary>
    public interface ICollectorHost : IDisposable
    {
        /// <summary>Stabiler Name für Observability/Logs (z.B. "HelloTracker").</summary>
        string Name { get; }

        /// <summary>Startet Event-Subscriptions + Background-Worker des gewrappten Collectors.</summary>
        void Start();

        /// <summary>Stoppt und gibt Ressourcen frei. Idempotent.</summary>
        void Stop();
    }
}
