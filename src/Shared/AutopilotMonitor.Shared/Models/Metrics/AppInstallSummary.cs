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

        /// <summary>
        /// Lifecycle status: Succeeded, Failed, InProgress, or empty.
        /// Empty (default) is a sentinel meaning "no status-relevant event observed in the current
        /// aggregation batch". Aggregators only set a real value when they see started / completed /
        /// failed / skipped. The storage layer omits the column from the upsert when this is empty
        /// so Merge-mode preserves any prior real value across batches that contain only progress
        /// or telemetry events. Readers fall back to "InProgress" when the column is missing on a
        /// row, so the UI/API contract remains stable.
        /// </summary>
        public string Status { get; set; } = string.Empty;

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

        // Delivery Optimization telemetry
        /// <summary>DO: total file size reported by DO</summary>
        public long DoFileSize { get; set; }

        /// <summary>DO: total bytes actually downloaded (may differ from DoFileSize on partial downloads)</summary>
        public long DoTotalBytesDownloaded { get; set; }

        /// <summary>DO: bytes from all peer sources</summary>
        public long DoBytesFromPeers { get; set; }

        /// <summary>DO: bytes from HTTP (CDN)</summary>
        public long DoBytesFromHttp { get; set; }

        /// <summary>DO: percentage from P2P (0-100)</summary>
        public int DoPercentPeerCaching { get; set; }

        /// <summary>DO: download mode (0=Background, 1=Foreground, 2=Bypass/LAN, 99=Simple)</summary>
        public int DoDownloadMode { get; set; } = -1;

        /// <summary>DO: actual download duration (TimeSpan string)</summary>
        public string DoDownloadDuration { get; set; } = string.Empty;

        /// <summary>DO: bytes from LAN peers</summary>
        public long DoBytesFromLanPeers { get; set; }

        /// <summary>DO: bytes from group peers</summary>
        public long DoBytesFromGroupPeers { get; set; }

        /// <summary>DO: bytes from internet peers</summary>
        public long DoBytesFromInternetPeers { get; set; }

        // App metadata (extracted from IME logs by ImeLogTracker)
        /// <summary>App version string (e.g. "1.7.00.4472"). Emitted in app_install_started.</summary>
        public string AppVersion { get; set; } = string.Empty;

        /// <summary>App type: Win32, MSI, WinGet, Store, LOB. Emitted in app_install_started.</summary>
        public string AppType { get; set; } = string.Empty;

        /// <summary>Install attempt number (1 = first try, 2+ = retry). Emitted in app_install_started.</summary>
        public int AttemptNumber { get; set; }

        /// <summary>Installer phase where failure occurred: Download, PreInstall, Install, PostInstall, Detection. Emitted in app_install_failed.</summary>
        public string InstallerPhase { get; set; } = string.Empty;

        /// <summary>Installer exit code (nullable – not every app type emits one). Emitted in app_install_completed/failed.</summary>
        public int? ExitCode { get; set; }

        /// <summary>Detection rule result after install: Detected, NotDetected. Emitted in app_install_completed/failed.</summary>
        public string DetectionResult { get; set; } = string.Empty;
    }
}
