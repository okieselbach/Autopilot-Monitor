namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Represents a log file path (or wildcard pattern) to include in the diagnostics ZIP package.
    /// Global (built-in) entries are defined by Galactic Admins; tenants may add their own.
    /// </summary>
    public class DiagnosticsLogPath
    {
        /// <summary>
        /// File system path or wildcard pattern.
        /// Environment variables are expanded by the agent.
        /// Wildcards are only allowed in the last path segment (e.g. "C:\Windows\Panther\*.log").
        /// </summary>
        public string Path { get; set; }

        /// <summary>
        /// Human-readable description shown in the portal.
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// True when defined globally by a Galactic Admin — displayed as read-only for tenants.
        /// False when added by the tenant itself.
        /// </summary>
        public bool IsBuiltIn { get; set; }
    }
}
