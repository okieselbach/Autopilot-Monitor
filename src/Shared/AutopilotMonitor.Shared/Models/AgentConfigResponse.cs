using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Response from the agent configuration endpoint
    /// Contains collector toggles and active gather rules for the tenant
    /// </summary>
    public class AgentConfigResponse
    {
        /// <summary>
        /// Semantic config version from backend.
        /// Used for debugging and future schema evolution.
        /// </summary>
        public int ConfigVersion { get; set; }

        /// <summary>
        /// Event upload debounce interval in seconds.
        /// </summary>
        public int UploadIntervalSeconds { get; set; } = 30;

        /// <summary>
        /// Whether to cleanup files on exit.
        /// </summary>
        public bool CleanupOnExit { get; set; } = false;

        /// <summary>
        /// Whether to self-destruct after enrollment completion.
        /// </summary>
        public bool SelfDestructOnComplete { get; set; } = false;

        /// <summary>
        /// Preserve logs during cleanup/self-destruct.
        /// </summary>
        public bool KeepLogFile { get; set; } = true;

        /// <summary>
        /// Optional path to IME pattern match log file.
        /// </summary>
        public string ImeMatchLogPath { get; set; } = @"C:\ProgramData\AutopilotMonitor\Logs\ime_pattern_matches.log";

        public CollectorConfiguration Collectors { get; set; }

        /// <summary>
        /// User-defined ad-hoc gather rules (minimal set, not for IME log parsing)
        /// </summary>
        public List<GatherRule> GatherRules { get; set; } = new List<GatherRule>();

        /// <summary>
        /// IME log regex patterns for smart enrollment tracking.
        /// Delivered from backend so patterns can be updated without agent rebuild.
        /// </summary>
        public List<ImeLogPattern> ImeLogPatterns { get; set; } = new List<ImeLogPattern>();

        /// <summary>
        /// Maximum consecutive authentication failures (401/403) before the agent shuts down.
        /// Prevents endless retry traffic when the device is not authorized.
        /// 0 = disabled (retry forever). Default: 5.
        /// </summary>
        public int MaxAuthFailures { get; set; } = 5;

        /// <summary>
        /// Maximum time in minutes the agent keeps retrying after the first auth failure.
        /// 0 = disabled (no time limit, only MaxAuthFailures applies). Default: 0.
        /// </summary>
        public int AuthFailureTimeoutMinutes { get; set; } = 0;
    }

    /// <summary>
    /// Configuration for optional agent collectors
    /// </summary>
    public class CollectorConfiguration
    {
        /// <summary>
        /// Enable CPU, memory, disk, network performance monitoring (always on for UI chart)
        /// Generates traffic: ~1 event per interval
        /// </summary>
        public bool EnablePerformanceCollector { get; set; } = true;

        /// <summary>
        /// Interval in seconds for performance snapshots
        /// Default: 60 seconds
        /// </summary>
        public int PerformanceIntervalSeconds { get; set; } = 60;

        /// <summary>
        /// Creates default collector configuration
        /// </summary>
        public static CollectorConfiguration CreateDefault()
        {
            return new CollectorConfiguration();
        }
    }
}
