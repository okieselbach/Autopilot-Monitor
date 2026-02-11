using System;

namespace AutopilotMonitor.Agent.Core.Configuration
{
    /// <summary>
    /// Agent configuration settings
    /// </summary>
    public class AgentConfiguration
    {
        /// <summary>
        /// Backend API base URL
        /// </summary>
        public string ApiBaseUrl { get; set; }

        /// <summary>
        /// Session identifier for this enrollment
        /// </summary>
        public string SessionId { get; set; }

        /// <summary>
        /// Tenant identifier
        /// </summary>
        public string TenantId { get; set; }

        /// <summary>
        /// Local spool directory path
        /// </summary>
        public string SpoolDirectory { get; set; }

        /// <summary>
        /// Agent log directory path
        /// </summary>
        public string LogDirectory { get; set; }

        /// <summary>
        /// Upload interval in seconds
        /// </summary>
        public int UploadIntervalSeconds { get; set; } = 30;

        /// <summary>
        /// Maximum batch size for events
        /// </summary>
        public int MaxBatchSize { get; set; } = 100;

        /// <summary>
        /// Whether to use client certificate authentication
        /// </summary>
        public bool UseClientCertAuth { get; set; } = true;

        /// <summary>
        /// Client certificate thumbprint (if using cert auth)
        /// </summary>
        public string ClientCertThumbprint { get; set; }

        /// <summary>
        /// Enable debug logging
        /// </summary>
        public bool EnableDebugLogging { get; set; } = false;

        /// <summary>
        /// Maximum retry attempts for failed uploads
        /// </summary>
        public int MaxRetryAttempts { get; set; } = 5;

        /// <summary>
        /// Enable Autopilot simulator for testing/demo (generates fake events)
        /// </summary>
        public bool EnableSimulator { get; set; } = false;

        /// <summary>
        /// Simulate enrollment failure (only used if EnableSimulator is true)
        /// </summary>
        public bool SimulateFailure { get; set; } = false;

        /// <summary>
        /// Whether to cleanup files on exit (delete C:\ProgramData\AutopilotMonitor)
        /// </summary>
        public bool CleanupOnExit { get; set; } = true;

        /// <summary>
        /// Whether to self-destruct when enrollment completes (remove Scheduled Task and all files)
        /// </summary>
        public bool SelfDestructOnComplete { get; set; } = true;

        /// <summary>
        /// Name of the Scheduled Task to remove during self-destruct
        /// </summary>
        public string ScheduledTaskName { get; set; } = "AutopilotMonitor-Agent";

        /// <summary>
        /// Whether to reboot the device after enrollment completes
        /// </summary>
        public bool RebootOnComplete { get; set; } = false;

        /// <summary>
        /// When true, the log directory is preserved after self-destruct/cleanup.
        /// All other files (binaries, config, spool, scheduled task) are still removed.
        /// </summary>
        public bool KeepLogFile { get; set; } = false;

        /// <summary>
        /// Whether to enable geo-location detection (queries external IP services)
        /// </summary>
        public bool EnableGeoLocation { get; set; } = true;

        /// <summary>
        /// Optional custom path to IME logs directory for testing.
        /// If set, overrides the default %ProgramData%\Microsoft\IntuneManagementExtension\Logs path.
        /// </summary>
        public string ImeLogPathOverride { get; set; }

        /// <summary>
        /// Optional path to write every IME log line that matched a pattern (for debugging).
        /// If empty, no match log is written. Example: %ProgramData%\AutopilotMonitor\Logs\ime-matches.log
        /// </summary>
        public string ImeMatchLogPath { get; set; }

        /// <summary>
        /// Path to directory with real IME log files for simulation replay.
        /// When set (with EnableSimulator), the simulator replays these logs with time compression
        /// instead of generating artificial events.
        /// </summary>
        public string SimulationLogDirectory { get; set; }

        /// <summary>
        /// Time compression factor for simulation mode.
        /// Higher values = faster replay. Default: 50 (e.g., 50 minutes of real logs = 1 minute of replay).
        /// Targeting 3-5 minute total enrollment replay.
        /// </summary>
        public double SimulationSpeedFactor { get; set; } = 50;

        /// <summary>
        /// Validates the configuration
        /// </summary>
        public bool IsValid()
        {
            return !string.IsNullOrEmpty(ApiBaseUrl) &&
                   !string.IsNullOrEmpty(SessionId) &&
                   !string.IsNullOrEmpty(TenantId);
        }
    }
}
