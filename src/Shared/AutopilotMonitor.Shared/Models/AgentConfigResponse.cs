using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Response from the agent configuration endpoint
    /// Contains collector toggles and active gather rules for the tenant
    /// </summary>
    public class AgentConfigResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public CollectorConfiguration Collectors { get; set; }
        public List<GatherRule> GatherRules { get; set; } = new List<GatherRule>();
        public int ConfigVersion { get; set; }

        /// <summary>
        /// How often the agent should re-fetch config (seconds)
        /// Default: 300 (5 minutes)
        /// </summary>
        public int RefreshIntervalSeconds { get; set; } = 300;
    }

    /// <summary>
    /// Configuration for optional agent collectors
    /// Core collectors (EventLogWatcher, RegistryMonitor, PhaseDetector, HelloDetector) are always on
    /// </summary>
    public class CollectorConfiguration
    {
        // ===== PERFORMANCE COLLECTOR =====

        /// <summary>
        /// Enable CPU, memory, disk, network performance monitoring
        /// Generates traffic: ~1 event per interval
        /// </summary>
        public bool EnablePerformanceCollector { get; set; } = false;

        /// <summary>
        /// Interval in seconds for performance snapshots
        /// Default: 60 seconds
        /// </summary>
        public int PerformanceIntervalSeconds { get; set; } = 60;

        // ===== DOWNLOAD PROGRESS COLLECTOR =====

        /// <summary>
        /// Enable IME/Intune app download progress tracking
        /// Generates traffic: ~1 event per interval per active download
        /// </summary>
        public bool EnableDownloadProgressCollector { get; set; } = false;

        /// <summary>
        /// Interval in seconds for download progress checks
        /// Default: 15 seconds
        /// </summary>
        public int DownloadProgressIntervalSeconds { get; set; } = 15;

        // ===== CERTIFICATE VALIDATION COLLECTOR =====

        /// <summary>
        /// Enable certificate chain validation for enrollment endpoints
        /// Generates traffic: low (runs at startup + on network phase)
        /// </summary>
        public bool EnableCertValidationCollector { get; set; } = false;

        // ===== ESP UI STATE COLLECTOR =====

        /// <summary>
        /// Enable ESP (Enrollment Status Page) UI state tracking
        /// Captures blocking apps, progress, status text
        /// Generates traffic: ~1 event per interval during ESP phases
        /// </summary>
        public bool EnableEspUiStateCollector { get; set; } = false;

        /// <summary>
        /// Interval in seconds for ESP UI state checks
        /// Default: 30 seconds
        /// </summary>
        public int EspUiStateIntervalSeconds { get; set; } = 30;

        /// <summary>
        /// Creates default collector configuration (all optional collectors off)
        /// </summary>
        public static CollectorConfiguration CreateDefault()
        {
            return new CollectorConfiguration();
        }
    }
}
