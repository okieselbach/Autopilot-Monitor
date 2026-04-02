namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Provider-agnostic alert rule for operational events.
    /// Stored as JSON array in AdminConfiguration.OpsAlertRulesJson.
    /// </summary>
    public class OpsAlertRule
    {
        /// <summary>Event type name, e.g. "ConsentFlowFailed".</summary>
        public string EventType { get; set; } = string.Empty;

        /// <summary>Minimum severity that triggers the alert: Info, Warning, Error, Critical.</summary>
        public string MinSeverity { get; set; } = "Error";

        /// <summary>Whether this rule is active.</summary>
        public bool Enabled { get; set; } = true;
    }
}
