namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Represents a log file path (or wildcard pattern) to include in the diagnostics ZIP package.
    /// Global (built-in) entries are defined by Global Admins; tenants may add their own.
    /// </summary>
    public class DiagnosticsLogPath
    {
        /// <summary>
        /// File system path or wildcard pattern.
        /// Environment variables are expanded by the agent.
        /// Wildcards are only allowed in the last path segment (e.g. "C:\Windows\Panther\*.log").
        /// </summary>
        public string Path { get; set; } = default!;

        /// <summary>
        /// Human-readable description shown in the portal.
        /// </summary>
        public string Description { get; set; } = default!;

        /// <summary>
        /// True when defined globally by a Global Admin — displayed as read-only for tenants.
        /// False when added by the tenant itself.
        /// </summary>
        public bool IsBuiltIn { get; set; }

        /// <summary>
        /// When true, the agent also collects matching files from subdirectories recursively.
        /// Subdirectory structure is preserved in the ZIP (e.g. AdditionalLogs/Logs/subfolder/file.log).
        /// Default is false (top-level only).
        /// </summary>
        public bool IncludeSubfolders { get; set; }
    }
}
