namespace AutopilotMonitor.Agent.V2.Core.Configuration
{
    /// <summary>
    /// Persisted await-enrollment configuration. Plan §4.x M4.6.α.
    /// <para>
    /// Saved to <c>%ProgramData%\AutopilotMonitor\await-enrollment.json</c> by <c>--install</c> mode
    /// so the Scheduled Task enters await-enrollment mode on startup (polls for the MDM certificate
    /// before spinning up the DecisionEngine). Deleted once the MDM certificate is found — subsequent
    /// restarts proceed normally.
    /// </para>
    /// </summary>
    public sealed class AwaitEnrollmentConfigFile
    {
        public int TimeoutMinutes { get; set; } = 480;
    }
}
