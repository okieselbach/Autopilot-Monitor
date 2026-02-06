using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Platform usage metrics response
    /// </summary>
    public class PlatformUsageMetrics
    {
        /// <summary>
        /// Session metrics
        /// </summary>
        public SessionMetrics Sessions { get; set; } = new();

        /// <summary>
        /// Tenant metrics
        /// </summary>
        public TenantMetrics Tenants { get; set; } = new();

        /// <summary>
        /// User metrics (requires Entra ID authentication)
        /// </summary>
        public UserMetrics Users { get; set; } = new();

        /// <summary>
        /// Performance metrics
        /// </summary>
        public PerformanceMetrics Performance { get; set; } = new();

        /// <summary>
        /// Hardware metrics
        /// </summary>
        public HardwareMetrics Hardware { get; set; } = new();

        /// <summary>
        /// When these metrics were computed
        /// </summary>
        public DateTime ComputedAt { get; set; }

        /// <summary>
        /// How long it took to compute (milliseconds)
        /// </summary>
        public int ComputeDurationMs { get; set; }

        /// <summary>
        /// Whether result is from cache
        /// </summary>
        public bool FromCache { get; set; }
    }

    public class SessionMetrics
    {
        public int Total { get; set; }
        public int Today { get; set; }
        public int Last7Days { get; set; }
        public int Last30Days { get; set; }
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public int InProgress { get; set; }
        public double SuccessRate { get; set; }
    }

    public class TenantMetrics
    {
        public int Total { get; set; }
        public int Active7Days { get; set; }
        public int Active30Days { get; set; }
    }

    public class UserMetrics
    {
        /// <summary>
        /// Total unique users (available after Entra ID integration)
        /// </summary>
        public int Total { get; set; }

        /// <summary>
        /// Daily logins across all users
        /// </summary>
        public int DailyLogins { get; set; }

        /// <summary>
        /// Active users in last 7 days
        /// </summary>
        public int Active7Days { get; set; }

        /// <summary>
        /// Active users in last 30 days
        /// </summary>
        public int Active30Days { get; set; }

        /// <summary>
        /// Note about availability
        /// </summary>
        public string Note { get; set; } = "";
    }

    public class PerformanceMetrics
    {
        public double AvgDurationMinutes { get; set; }
        public double MedianDurationMinutes { get; set; }
        public double P95DurationMinutes { get; set; }
        public double P99DurationMinutes { get; set; }
    }

    public class HardwareMetrics
    {
        public List<HardwareCount> TopManufacturers { get; set; } = new();
        public List<HardwareCount> TopModels { get; set; } = new();
    }

    public class HardwareCount
    {
        public string Name { get; set; } = string.Empty;
        public int Count { get; set; }
        public double Percentage { get; set; }
    }
}
