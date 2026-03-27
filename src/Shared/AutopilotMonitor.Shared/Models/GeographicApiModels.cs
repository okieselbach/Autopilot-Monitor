using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Response containing geographic performance metrics aggregated by location.
    /// </summary>
    public class GeographicMetricsResponse
    {
        public bool Success { get; set; }
        public List<LocationMetrics> Locations { get; set; } = new();
        public GlobalAverages GlobalAverages { get; set; } = new();
        public DateTime ComputedAt { get; set; }
        public int TotalSessions { get; set; }
        public int LocationsWithData { get; set; }
        /// <summary>Whether geo-location collection is enabled for this tenant</summary>
        public bool GeoLocationEnabled { get; set; } = true;
    }

    /// <summary>
    /// Performance metrics for a single geographic location.
    /// </summary>
    public class LocationMetrics
    {
        public string LocationKey { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;
        public string Region { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string Loc { get; set; } = string.Empty;

        public int SessionCount { get; set; }
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public double SuccessRate { get; set; }

        public double AvgDurationMinutes { get; set; }
        public double MedianDurationMinutes { get; set; }
        public double P95DurationMinutes { get; set; }

        /// <summary>Average number of apps installed per session at this location</summary>
        public double AvgAppCount { get; set; }
        /// <summary>Average minutes per app (AvgDurationMinutes / AvgAppCount)</summary>
        public double MinutesPerApp { get; set; }
        /// <summary>Normalized score: 100 = global median, lower is better</summary>
        public double AppLoadScore { get; set; }

        /// <summary>Average download throughput in bytes/sec at this location</summary>
        public double AvgThroughputBytesPerSec { get; set; }
        public long TotalDownloadBytes { get; set; }

        /// <summary>Percentage difference from global avg duration (negative = faster)</summary>
        public double DurationVsGlobalPct { get; set; }
        /// <summary>Percentage difference from global avg throughput (positive = faster)</summary>
        public double ThroughputVsGlobalPct { get; set; }

        public bool IsOutlier { get; set; }
        /// <summary>"fast", "slow", or null</summary>
        public string? OutlierDirection { get; set; }
    }

    /// <summary>
    /// Global average benchmarks for geographic comparison.
    /// </summary>
    public class GlobalAverages
    {
        public double AvgDurationMinutes { get; set; }
        public double MedianDurationMinutes { get; set; }
        public double AvgMinutesPerApp { get; set; }
        public double AvgThroughputBytesPerSec { get; set; }
        public double StdDevDurationMinutes { get; set; }
    }
}
