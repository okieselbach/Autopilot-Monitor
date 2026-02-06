namespace AutopilotMonitor.Shared
{
    /// <summary>
    /// Shared constants across all projects
    /// </summary>
    public static class Constants
    {
        /// <summary>
        /// Agent version
        /// </summary>
        public const string AgentVersion = "1.0.0-phase1";

        /// <summary>
        /// Registry path for agent configuration
        /// </summary>
        public const string RegistryPath = @"SOFTWARE\AutopilotMonitor";

        /// <summary>
        /// Local spool directory for offline queueing
        /// </summary>
        public const string SpoolDirectory = @"%ProgramData%\AutopilotMonitor\Spool";

        /// <summary>
        /// Agent log directory
        /// </summary>
        public const string LogDirectory = @"%ProgramData%\AutopilotMonitor\Logs";

        /// <summary>
        /// Maximum batch size for events
        /// </summary>
        public const int MaxBatchSize = 100;

        /// <summary>
        /// Maximum batch age in seconds
        /// </summary>
        public const int MaxBatchAgeSeconds = 30;

        /// <summary>
        /// Maximum retry attempts for failed uploads
        /// </summary>
        public const int MaxRetryAttempts = 5;

        /// <summary>
        /// API endpoint paths
        /// </summary>
        public static class ApiEndpoints
        {
            public const string RegisterSession = "/api/sessions/register";
            public const string IngestEvents = "/api/events/ingest";
            public const string UploadBundle = "/api/bundles/upload";
            public const string GetAgentConfig = "/api/agent/config";
            public const string GatherRules = "/api/gather-rules";
            public const string AnalyzeRules = "/api/analyze-rules";
        }

        /// <summary>
        /// Event types
        /// </summary>
        public static class EventTypes
        {
            public const string PhaseTransition = "phase_transition";
            public const string AppInstallStart = "app_install_start";
            public const string AppInstallComplete = "app_install_complete";
            public const string AppInstallFailed = "app_install_failed";
            public const string NetworkStateChange = "network_state_change";
            public const string ErrorDetected = "error_detected";
            public const string PerformanceSnapshot = "performance_snapshot";
            public const string LogEntry = "log_entry";
            public const string EspStateChange = "esp_state_change";
            public const string DownloadProgress = "download_progress";
            public const string CertValidation = "cert_validation";
            public const string EspUiState = "esp_ui_state";
            public const string GatherResult = "gather_result";
        }

        /// <summary>
        /// Event sources
        /// </summary>
        public static class EventSources
        {
            public const string Agent = "Agent";
            public const string IME = "IME";
            public const string EventLog = "EventLog";
            public const string Registry = "Registry";
            public const string WMI = "WMI";
            public const string Network = "Network";
        }

        /// <summary>
        /// Log file paths to monitor
        /// </summary>
        public static class LogPaths
        {
            public const string IMELog = @"C:\Windows\CCM\Logs\Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider\Admin.evtx";
            public const string ModernDeployment = @"C:\Windows\Logs\Autopilot\ModernDeployment-Diagnostics-Provider.log";
        }

        /// <summary>
        /// Azure Table Storage table names
        /// </summary>
        public static class TableNames
        {
            public const string Sessions = "sessions";
            public const string Events = "events";
            public const string RuleResults = "ruleresults";
            public const string GatherRules = "gatherrules";
            public const string AnalyzeRules = "analyzerules";
        }

        /// <summary>
        /// Azure Blob Storage container names
        /// </summary>
        public static class ContainerNames
        {
            public const string Bundles = "bundles";
            public const string Screenshots = "screenshots";
            public const string Logs = "logs";
        }
    }
}
