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
        /// Whether to self-destruct after enrollment completion (remove Scheduled Task and all files).
        /// </summary>
        public bool SelfDestructOnComplete { get; set; } = true;

        /// <summary>
        /// Preserve logs during self-destruct.
        /// </summary>
        public bool KeepLogFile { get; set; } = false;

        /// <summary>
        /// Whether to enable geo-location detection.
        /// </summary>
        public bool EnableGeoLocation { get; set; } = true;

        /// <summary>
        /// Whether to write a log of every IME log line matched by a pattern.
        /// When true, the default path is used: Constants.ImeMatchLogPath.
        /// </summary>
        public bool EnableImeMatchLog { get; set; } = false;

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

        /// <summary>
        /// Log verbosity level for the agent.
        /// "Info" = normal messages, "Debug" = component state/decisions, "Verbose" = per-event tracing.
        /// Default: "Info"
        /// </summary>
        public string LogLevel { get; set; } = "Info";

        /// <summary>
        /// Whether to reboot the device after enrollment completes (and cleanup/self-destruct).
        /// Default: false
        /// </summary>
        public bool RebootOnComplete { get; set; } = false;

        /// <summary>
        /// Delay in seconds before the reboot is initiated (shutdown.exe /r /t X).
        /// Gives the user a short window to see what is happening.
        /// Default: 10 seconds
        /// </summary>
        public int RebootDelaySeconds { get; set; } = 10;

        /// <summary>
        /// Maximum number of events per upload batch.
        /// Default: 100
        /// </summary>
        public int MaxBatchSize { get; set; } = 100;

        /// <summary>
        /// Whether diagnostics upload is configured for this tenant.
        /// When true, the agent should request a short-lived upload URL via the API just before uploading.
        /// The SAS URL itself is never included in this config response — it is fetched on-demand.
        /// </summary>
        public bool DiagnosticsUploadEnabled { get; set; } = false;

        /// <summary>
        /// When to upload diagnostics packages: "Off", "Always", "OnFailure".
        /// Default: "Off"
        /// </summary>
        public string DiagnosticsUploadMode { get; set; } = "Off";
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
        /// Default: 30 seconds
        /// </summary>
        public int PerformanceIntervalSeconds { get; set; } = 30;

        /// <summary>
        /// Creates default collector configuration
        /// </summary>
        public static CollectorConfiguration CreateDefault()
        {
            return new CollectorConfiguration();
        }
    }
}
