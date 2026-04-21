namespace AutopilotMonitor.Agent.V2.Core.Configuration
{
    /// <summary>
    /// Persisted bootstrap configuration for OOBE-bootstrapped agents. Plan §4.x M4.6.α.
    /// <para>
    /// Saved to <c>%ProgramData%\AutopilotMonitor\bootstrap-config.json</c> by <c>--install</c> mode
    /// so the Scheduled Task (whose command line has no args) can pick up the bootstrap token and
    /// tenant id on the first post-install run.
    /// </para>
    /// </summary>
    public sealed class BootstrapConfigFile
    {
        public string BootstrapToken { get; set; }
        public string TenantId { get; set; }
    }
}
