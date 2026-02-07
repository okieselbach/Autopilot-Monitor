using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Per-app installation summary, written during event ingestion.
    /// Enables fleet-level app metrics without scanning raw events.
    /// </summary>
    public class AppInstallSummary
    {
        public string AppName { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;

        /// <summary>Succeeded, Failed, InProgress</summary>
        public string Status { get; set; } = "InProgress";

        /// <summary>Total installation duration in seconds (from start to complete/failed)</summary>
        public int DurationSeconds { get; set; }

        /// <summary>Total download size in bytes</summary>
        public long DownloadBytes { get; set; }

        /// <summary>Download duration in seconds</summary>
        public int DownloadDurationSeconds { get; set; }

        /// <summary>Error code if failed</summary>
        public string FailureCode { get; set; } = string.Empty;

        /// <summary>Error message if failed</summary>
        public string FailureMessage { get; set; } = string.Empty;

        /// <summary>When this app install started</summary>
        public DateTime StartedAt { get; set; }

        /// <summary>When this app install completed or failed</summary>
        public DateTime? CompletedAt { get; set; }
    }
}
