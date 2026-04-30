#nullable enable
using System;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Surface for components that observe the Shell-Core WhiteGlove pre-provisioning success
    /// signal (Windows Event 62407) and need to forward it to dependents that don't share the
    /// host's lifecycle directly. Implemented by <see cref="EspAndHelloHost"/> in production
    /// and by tiny test stubs in <see cref="WhiteGloveInventoryTrigger"/> unit tests.
    /// </summary>
    internal interface IWhiteGloveCompletedSource
    {
        /// <summary>
        /// Fires when the agent observes a successful WhiteGlove sealing — i.e. while the
        /// OOBE "Continue / Reseal" dialog is shown and the admin has not yet clicked Reseal.
        /// </summary>
        event EventHandler? WhiteGloveCompleted;
    }
}
