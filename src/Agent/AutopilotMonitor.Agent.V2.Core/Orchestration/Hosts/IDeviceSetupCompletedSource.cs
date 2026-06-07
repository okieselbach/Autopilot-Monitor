#nullable enable
using System;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Surface for components that observe the DeviceSetup-phase completion signal and need to
    /// forward it to dependents that don't share the host's lifecycle directly. Implemented by
    /// <see cref="EspAndHelloHost"/> in production and by tiny test stubs in
    /// <see cref="AutoLogonDeviceSetupTrigger"/> unit tests.
    /// </summary>
    internal interface IDeviceSetupCompletedSource
    {
        /// <summary>
        /// Fires once when DeviceSetup provisioning resolves with success (or the fallback
        /// confirmed) — i.e. the device phase of the ESP is done. Fire-once upstream
        /// (<c>ProvisioningStatusTracker</c> guards against duplicate emissions).
        /// </summary>
        event EventHandler? DeviceSetupProvisioningComplete;
    }
}
