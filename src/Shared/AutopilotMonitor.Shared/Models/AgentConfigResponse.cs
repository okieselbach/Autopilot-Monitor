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

        /// <summary>
        /// User-defined ad-hoc gather rules (minimal set, not for IME log parsing)
        /// </summary>
        public List<GatherRule> GatherRules { get; set; } = new List<GatherRule>();

        /// <summary>
        /// IME log regex patterns for smart enrollment tracking.
        /// Delivered from backend so patterns can be updated without agent rebuild.
        /// </summary>
        public List<ImeLogPattern> ImeLogPatterns { get; set; } = new List<ImeLogPattern>();

        public int ConfigVersion { get; set; }

        /// <summary>
        /// How often the agent should re-fetch config (seconds)
        /// Default: 300 (5 minutes)
        /// </summary>
        public int RefreshIntervalSeconds { get; set; } = 300;
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
