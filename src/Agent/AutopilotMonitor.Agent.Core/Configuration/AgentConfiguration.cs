using System;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Shared;

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
        /// Log verbosity level: Info (default), Debug, Verbose.
        /// Overridable via remote config.
        /// </summary>
        public AgentLogLevel LogLevel { get; set; } = AgentLogLevel.Info;

        /// <summary>
        /// Maximum retry attempts for failed uploads
        /// </summary>
        public int MaxRetryAttempts { get; set; } = 5;

        /// <summary>
        /// Whether to self-destruct when enrollment completes (remove Scheduled Task and all files).
        /// This is the only cleanup mode — SelfDestruct always removes task + files.
        /// </summary>
        public bool SelfDestructOnComplete { get; set; } = true;

        /// <summary>
        /// Name of the Scheduled Task to remove during self-destruct
        /// </summary>
        public string ScheduledTaskName { get; set; } = Constants.ScheduledTaskName;

        /// <summary>
        /// Whether to reboot the device after enrollment completes
        /// </summary>
        public bool RebootOnComplete { get; set; } = false;

        /// <summary>
        /// Delay in seconds before the reboot is initiated (shutdown.exe /r /t X).
        /// Default: 10 seconds
        /// </summary>
        public int RebootDelaySeconds { get; set; } = 10;

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
        /// Path to directory with real IME log files for log replay.
        /// When set, the agent replays these logs with time compression instead of reading live logs.
        /// </summary>
        public string ReplayLogDir { get; set; }

        /// <summary>
        /// Time compression factor for log replay.
        /// Higher values = faster replay. Default: 50 (e.g., 50 minutes of real logs = 1 minute of replay).
        /// </summary>
        public double ReplaySpeedFactor { get; set; } = 50;

        /// <summary>
        /// Wait time in seconds after ESP exit before marking Hello as skipped.
        /// When ESP exits, we wait this duration for Hello wizard to start (event 62404).
        /// Default: 30 seconds (reasonable for systems under load).
        /// If Hello wizard starts within this window, we continue waiting for Hello completion (300/301).
        /// </summary>
        public int HelloWaitTimeoutSeconds { get; set; } = 30;

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
