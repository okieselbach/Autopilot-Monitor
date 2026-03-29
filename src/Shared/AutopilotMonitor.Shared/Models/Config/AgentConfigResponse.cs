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

        public CollectorConfiguration Collectors { get; set; } = default!;

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
        /// "Info" = normal messages, "Debug" = component state/decisions, "Verbose" = per-event tracing, "Trace" = full diagnostic output.
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
        /// Whether to show a visual enrollment summary dialog to the end user
        /// after enrollment completes (success or failure).
        /// Default: false (opt-in)
        /// </summary>
        public bool ShowEnrollmentSummary { get; set; } = false;

        /// <summary>
        /// Auto-close timeout in seconds for the enrollment summary dialog.
        /// 0 = no auto-close. Default: 60
        /// </summary>
        public int EnrollmentSummaryTimeoutSeconds { get; set; } = 60;

        /// <summary>
        /// Optional URL to a branding image displayed as a banner at the top of the enrollment summary dialog.
        /// Expected size: 540 x 80 px. Larger images will be center-cropped.
        /// null = no banner.
        /// </summary>
        public string EnrollmentSummaryBrandingImageUrl { get; set; } = default!;

        /// <summary>
        /// Maximum time in seconds the agent retries launching the enrollment summary dialog
        /// when the user's desktop is locked by a credential UI (e.g. Windows Hello).
        /// 0 = no retry (single attempt). Default: 120 (2 minutes).
        /// </summary>
        public int EnrollmentSummaryLaunchRetrySeconds { get; set; } = 120;

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

        /// <summary>
        /// Merged list of log paths/wildcards to include in the diagnostics ZIP package.
        /// Global entries (IsBuiltIn=true) come first, followed by tenant-specific additions.
        /// The agent validates each path against DiagnosticsPathGuards before collection.
        /// </summary>
        public List<DiagnosticsLogPath> DiagnosticsLogPaths { get; set; } = new List<DiagnosticsLogPath>();

        /// <summary>
        /// Configuration for agent-side security and configuration analyzers.
        /// Controls which analyzers run and their per-analyzer parameters.
        /// </summary>
        public AnalyzerConfiguration Analyzers { get; set; } = new AnalyzerConfiguration();

        /// <summary>
        /// Whether the agent should send Trace-severity events to the backend.
        /// Trace events capture key agent decisions (e.g. "AccountSetup suppressed — no real user profile")
        /// for backend troubleshooting without relying on the agent log file.
        /// Default: true (on in preview).
        /// </summary>
        public bool SendTraceEvents { get; set; } = true;

        /// <summary>
        /// When true, agent guardrails are relaxed: all registry, WMI, and command targets are allowed.
        /// File/diagnostics paths allow everything except C:\Users.
        /// Default: false.
        /// </summary>
        public bool UnrestrictedMode { get; set; } = false;

        /// <summary>
        /// SHA-256 hash (lowercase hex) of the latest published agent ZIP, provided by the backend.
        /// Used for integrity verification during self-update as a second trust channel
        /// (separate from the hash in version.json on blob storage).
        /// null = backend does not have a hash (backward compat with older backend deployments).
        /// </summary>
        public string LatestAgentSha256 { get; set; } = default!;

        /// <summary>
        /// NTP server address for time check during enrollment.
        /// Default: "time.windows.com"
        /// </summary>
        public string NtpServer { get; set; } = "time.windows.com";

        /// <summary>
        /// Whether to automatically set the device timezone based on IP geolocation.
        /// Requires EnableGeoLocation to be true. Uses tzutil /s to apply.
        /// Default: false
        /// </summary>
        public bool EnableTimezoneAutoSet { get; set; } = false;
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
        /// Idle timeout in minutes for periodic collectors (Performance, AgentSelfMetrics).
        /// Collectors stop after this many minutes without real enrollment activity and
        /// restart automatically when new activity is detected.
        /// 0 = disabled (collectors run indefinitely). Default: 15 minutes.
        /// </summary>
        public int CollectorIdleTimeoutMinutes { get; set; } = 15;

        /// <summary>
        /// Enable the agent self-metrics collector (process CPU, memory, network traffic).
        /// Default: true
        /// </summary>
        public bool EnableAgentSelfMetrics { get; set; } = true;

        /// <summary>
        /// Interval in seconds for agent self-metrics snapshots.
        /// Default: 60 seconds
        /// </summary>
        public int AgentSelfMetricsIntervalSeconds { get; set; } = 60;

        /// <summary>
        /// Seconds to wait for the Windows Hello wizard after ESP exit.
        /// Default: 30 seconds
        /// </summary>
        public int HelloWaitTimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Maximum agent lifetime in minutes. Safety net to prevent zombie agents.
        /// 0 = disabled (no lifetime limit). Default: 360 (6 hours).
        /// </summary>
        public int AgentMaxLifetimeMinutes { get; set; } = 360;

        /// <summary>
        /// Creates default collector configuration
        /// </summary>
        public static CollectorConfiguration CreateDefault()
        {
            return new CollectorConfiguration();
        }
    }

    /// <summary>
    /// Configuration for agent-side analyzers (security and configuration checks).
    /// Analyzers differ from collectors: they run checks, produce a confidence-scored finding,
    /// and emit a single structured event — rather than streaming raw telemetry data.
    /// </summary>
    public class AnalyzerConfiguration
    {
        /// <summary>
        /// Whether to run the LocalAdminAnalyzer at startup and shutdown.
        /// Detects pre-enrollment local admin account creation (Autopilot bypass technique).
        /// Default: true
        /// </summary>
        public bool EnableLocalAdminAnalyzer { get; set; } = true;

        /// <summary>
        /// Additional local account names considered expected on a newly enrolled device.
        /// These are merged (union) with the built-in defaults:
        /// Administrator, Guest, DefaultAccount, WDAGUtilityAccount, Public, Default.
        /// Any local account not in the merged list will be flagged.
        /// Default: empty list (built-in defaults only)
        /// </summary>
        public List<string> LocalAdminAllowedAccounts { get; set; } = new List<string>();

        /// <summary>
        /// Whether to run the SoftwareInventoryAnalyzer at startup and shutdown.
        /// Collects installed software registry inventory, normalizes entries, and
        /// emits a baseline snapshot (startup) plus delta (shutdown) for vulnerability insight.
        /// Default: true
        /// </summary>
        public bool EnableSoftwareInventoryAnalyzer { get; set; } = false;
    }
}
