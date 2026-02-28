namespace AutopilotMonitor.Shared
{
    /// <summary>
    /// Shared constants across all projects
    /// </summary>
    public static class Constants
    {
        // -----------------------------------------------------------------------
        // Agent runtime defaults
        // -----------------------------------------------------------------------

        /// <summary>
        /// Base data directory for all agent files (spool, logs, state)
        /// </summary>
        public const string AgentDataDirectory = @"%ProgramData%\AutopilotMonitor";

        /// <summary>
        /// Local spool directory for offline queueing
        /// </summary>
        public const string SpoolDirectory = @"%ProgramData%\AutopilotMonitor\Spool";

        /// <summary>
        /// Agent log directory
        /// </summary>
        public const string LogDirectory = @"%ProgramData%\AutopilotMonitor\Logs";

        /// <summary>
        /// Agent state directory (enrollment complete marker, IME tracker state, etc.)
        /// </summary>
        public const string StateDirectory = @"%ProgramData%\AutopilotMonitor\State";

        /// <summary>
        /// Default path for the IME pattern match log file (debugging/diagnostics)
        /// </summary>
        public const string ImeMatchLogPath = @"%ProgramData%\AutopilotMonitor\Logs\ime_pattern_matches.log";

        /// <summary>
        /// Scheduled Task name used to run the agent as SYSTEM
        /// </summary>
        public const string ScheduledTaskName = "AutopilotMonitor-Agent";

        /// <summary>
        /// Default backend API base URL (overridable via AUTOPILOT_MONITOR_API env var or --api-url CLI arg)
        /// </summary>
        public const string ApiBaseUrl = "https://autopilotmonitor-api.azurewebsites.net";

        // -----------------------------------------------------------------------
        // Upload / batching defaults
        // -----------------------------------------------------------------------

        /// <summary>
        /// Maximum number of events per upload batch
        /// </summary>
        public const int MaxBatchSize = 100;

        /// <summary>
        /// Default upload interval in seconds (fallback timer; normal path uses FileSystemWatcher)
        /// </summary>
        public const int DefaultUploadIntervalSeconds = 30;

        // -----------------------------------------------------------------------
        // API endpoint paths (relative to ApiBaseUrl)
        // -----------------------------------------------------------------------

        /// <summary>
        /// API endpoint paths used by the agent
        /// </summary>
        public static class ApiEndpoints
        {
            public const string RegisterSession          = "/api/agent/register-session";
            public const string IngestEvents             = "/api/agent/ingest";
            public const string GetAgentConfig           = "/api/agent/config";
            public const string GatherRules              = "/api/rules/gather";
            public const string AnalyzeRules             = "/api/rules/analyze";
            public const string GetDiagnosticsUploadUrl  = "/api/agent/upload-url";
            public const string BlockDevice              = "/api/devices/block";
            public const string GetBlockedDevices        = "/api/devices/blocked";
            public const string ImeLogPatterns           = "/api/rules/ime-log-patterns";
            public const string ReseedFromGitHub         = "/api/rules/reseed-from-github";
        }

        // -----------------------------------------------------------------------
        // Event types emitted by the agent
        // -----------------------------------------------------------------------

        /// <summary>
        /// Event type identifiers for EnrollmentEvent.EventType
        /// </summary>
        public static class EventTypes
        {
            public const string PhaseTransition     = "phase_transition";
            public const string AppInstallStart     = "app_install_started";
            public const string AppInstallComplete  = "app_install_completed";
            public const string AppInstallFailed    = "app_install_failed";
            public const string AppDownloadStarted  = "app_download_started";
            public const string AppInstallSkipped   = "app_install_skipped";
            public const string NetworkStateChange  = "network_state_change";
            public const string ErrorDetected       = "error_detected";
            public const string PerformanceSnapshot = "performance_snapshot";
            public const string LogEntry            = "log_entry";
            public const string EspStateChange      = "esp_state_change";
            public const string DownloadProgress    = "download_progress";
            public const string CertValidation      = "cert_validation";
            public const string EspUiState          = "esp_ui_state";
            public const string GatherResult        = "gather_result";
            public const string WhiteGloveComplete  = "whiteglove_complete";
        }

        // -----------------------------------------------------------------------
        // Event sources
        // -----------------------------------------------------------------------

        /// <summary>
        /// Event source identifiers for EnrollmentEvent.Source
        /// </summary>
        public static class EventSources
        {
            public const string Agent    = "Agent";
            public const string IME      = "IME";
            public const string Registry = "Registry";
            public const string WMI      = "WMI";
            public const string Network  = "Network";
        }

        // -----------------------------------------------------------------------
        // Azure Table Storage table names
        // All table names are defined here centrally and initialized at application startup
        // -----------------------------------------------------------------------

        /// <summary>
        /// Azure Table Storage table names
        /// </summary>
        public static class TableNames
        {
            // Core data tables
            public const string Sessions       = "Sessions";
            public const string Events         = "Events";
            public const string AuditLogs      = "AuditLogs";
            public const string UsageMetrics   = "UsageMetrics";
            public const string UserActivity   = "UserActivity";

            // Rules engine tables
            public const string RuleResults    = "RuleResults";
            public const string GatherRules    = "GatherRules";
            public const string AnalyzeRules   = "AnalyzeRules";
            public const string ImeLogPatterns = "ImeLogPatterns";
            public const string RuleStates     = "RuleStates";

            // App metrics tables
            public const string AppInstallSummaries = "AppInstallSummaries";
            public const string PlatformStats       = "PlatformStats";

            // Configuration tables
            public const string TenantConfiguration = "TenantConfiguration";
            public const string AdminConfiguration  = "AdminConfiguration";

            // Admin tables
            public const string GalacticAdmins = "GalacticAdmins";
            public const string TenantAdmins   = "TenantAdmins";

            // Preview gating (temporary — remove after GA)
            public const string PreviewWhitelist = "PreviewWhitelist";
            public const string PreviewConfig    = "PreviewConfig";

            // Device blocking
            public const string BlockedDevices = "BlockedDevices";

            /// <summary>
            /// Returns all table names for initialization
            /// </summary>
            public static string[] All => new[]
            {
                Sessions,
                Events,
                AuditLogs,
                UsageMetrics,
                UserActivity,
                RuleResults,
                GatherRules,
                AnalyzeRules,
                ImeLogPatterns,
                RuleStates,
                AppInstallSummaries,
                PlatformStats,
                TenantConfiguration,
                AdminConfiguration,
                GalacticAdmins,
                TenantAdmins,
                PreviewWhitelist,
                PreviewConfig,
                BlockedDevices
            };
        }
    }
}
