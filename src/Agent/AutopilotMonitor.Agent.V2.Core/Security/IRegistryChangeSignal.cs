using System;

namespace AutopilotMonitor.Agent.V2.Core.Security
{
    /// <summary>
    /// Abstraction over a (possibly composite) registry change source. Lets
    /// <see cref="TenantIdAwaiter"/> be unit-tested without Win32 dependencies —
    /// production wires this to one or more <see cref="Monitoring.Runtime.RegistryWatcher"/>
    /// instances, tests wire a fake that raises <see cref="Changed"/> on demand.
    /// </summary>
    public interface IRegistryChangeSignal : IDisposable
    {
        /// <summary>Raised whenever any underlying watched key reports a change.</summary>
        event EventHandler Changed;

        /// <summary>Begins observing. Idempotent: calling twice on a started signal is a no-op.</summary>
        void Start();
    }
}
