using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models.Metrics
{
    /// <summary>
    /// SLA metrics response for a given tenant and time window.
    /// </summary>
    public class SlaMetricsResponse
    {
        // Target echo (so the dashboard knows the configured targets)
        public decimal? TargetSuccessRate { get; set; }
        public int? TargetMaxDurationMinutes { get; set; }
        public decimal? TargetAppInstallSuccessRate { get; set; }

        /// <summary>Current month SLA snapshot.</summary>
        public SlaSnapshot CurrentMonth { get; set; } = new();

        /// <summary>Monthly trend (newest first).</summary>
        public List<SlaMonthlyTrend> MonthlyTrend { get; set; } = new();

        /// <summary>Sessions that breached SLA targets (failed or exceeded duration).</summary>
        public List<SlaViolatorSession> Violators { get; set; } = new();

        /// <summary>App install SLA snapshot (null if no app install target configured).</summary>
        public AppInstallSlaSnapshot? AppInstallSla { get; set; }

        public DateTime ComputedAt { get; set; }
        public bool FromCache { get; set; }
        public int ComputeDurationMs { get; set; }
    }

    /// <summary>
    /// SLA compliance snapshot for a single month.
    /// </summary>
    public class SlaSnapshot
    {
        /// <summary>Month identifier, e.g. "2026-04".</summary>
        public string Month { get; set; } = default!;

        public int TotalCompleted { get; set; }
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public double SuccessRate { get; set; }
        public double AvgDurationMinutes { get; set; }
        public double P95DurationMinutes { get; set; }

        /// <summary>Number of sessions that exceeded the duration target.</summary>
        public int DurationViolationCount { get; set; }

        /// <summary>Whether the success rate target is met.</summary>
        public bool SuccessRateMet { get; set; }

        /// <summary>Whether the P95 duration target is met.</summary>
        public bool DurationTargetMet { get; set; }
    }

    /// <summary>
    /// SLA compliance trend entry for one month.
    /// </summary>
    public class SlaMonthlyTrend
    {
        /// <summary>Month identifier, e.g. "2026-04".</summary>
        public string Month { get; set; } = default!;

        public double SuccessRate { get; set; }
        public double P95DurationMinutes { get; set; }
        public double AppInstallSuccessRate { get; set; }
        public int TotalCompleted { get; set; }

        public bool SuccessRateMet { get; set; }
        public bool DurationTargetMet { get; set; }
        public bool AppInstallTargetMet { get; set; }
    }

    /// <summary>
    /// A session that violated SLA targets.
    /// </summary>
    public class SlaViolatorSession
    {
        public string SessionId { get; set; } = default!;
        public string TenantId { get; set; } = default!;
        public string DeviceName { get; set; } = default!;
        public string SerialNumber { get; set; } = default!;
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public int? DurationSeconds { get; set; }
        public int Status { get; set; }
        public string? FailureReason { get; set; }

        /// <summary>"Failed", "DurationExceeded", or "Both".</summary>
        public string ViolationType { get; set; } = default!;
    }

    /// <summary>
    /// App install SLA snapshot.
    /// </summary>
    public class AppInstallSlaSnapshot
    {
        public int TotalInstalls { get; set; }
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public double SuccessRate { get; set; }
        public bool TargetMet { get; set; }

        /// <summary>Top failing apps by failure count.</summary>
        public List<TopFailingApp> TopFailingApps { get; set; } = new();
    }

    /// <summary>
    /// An app with a high failure rate.
    /// </summary>
    public class TopFailingApp
    {
        public string AppName { get; set; } = default!;
        public int FailCount { get; set; }
        public int TotalCount { get; set; }
        public double SuccessRate { get; set; }
    }
}
