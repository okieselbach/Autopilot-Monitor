using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Persistent per-tenant SLA breach state. One row per tenant in the
    /// <c>SlaTenantStatus</c> table (PartitionKey = tenantId, RowKey = "status").
    /// Per-breach-type fields are stored as flat property prefixes to keep
    /// the per-cycle write atomic and the GA cross-tenant scan trivial.
    /// </summary>
    public class SlaTenantStatus
    {
        public const string StatusRowKey = "status";

        public string TenantId { get; set; } = string.Empty;

        /// <summary>UTC timestamp of the last SLA evaluation that touched this row.</summary>
        public DateTime LastEvaluatedAt { get; set; }

        // ── SuccessRate ───────────────────────────────────────────────────────
        public bool SuccessRate_IsActive { get; set; }
        public double? SuccessRate_CurrentValue { get; set; }
        public double? SuccessRate_TargetValue { get; set; }
        public double? SuccessRate_ThresholdValue { get; set; }
        public int? SuccessRate_TotalSessions { get; set; }
        public int? SuccessRate_FailedSessions { get; set; }
        public DateTime? SuccessRate_FirstBreachAt { get; set; }
        public DateTime? SuccessRate_LastBreachAt { get; set; }
        public DateTime? SuccessRate_LastNotifiedAt { get; set; }
        public DateTime? SuccessRate_ResolvedAt { get; set; }

        // ── Duration ──────────────────────────────────────────────────────────
        public bool Duration_IsActive { get; set; }
        public double? Duration_CurrentP95Minutes { get; set; }
        public int? Duration_TargetMinutes { get; set; }
        public int? Duration_TotalSessions { get; set; }
        public DateTime? Duration_FirstBreachAt { get; set; }
        public DateTime? Duration_LastBreachAt { get; set; }
        public DateTime? Duration_LastNotifiedAt { get; set; }
        public DateTime? Duration_ResolvedAt { get; set; }

        // ── AppInstall ────────────────────────────────────────────────────────
        public bool AppInstall_IsActive { get; set; }
        public double? AppInstall_CurrentRate { get; set; }
        public double? AppInstall_TargetRate { get; set; }
        public string? AppInstall_TopFailingApp { get; set; }
        public DateTime? AppInstall_FirstBreachAt { get; set; }
        public DateTime? AppInstall_LastBreachAt { get; set; }
        public DateTime? AppInstall_LastNotifiedAt { get; set; }
        public DateTime? AppInstall_ResolvedAt { get; set; }

        // ── ConsecutiveFailures ───────────────────────────────────────────────
        public bool ConsecutiveFailures_IsActive { get; set; }
        public int? ConsecutiveFailures_Count { get; set; }
        public string? ConsecutiveFailures_LastDevice { get; set; }
        public string? ConsecutiveFailures_LastReason { get; set; }
        public DateTime? ConsecutiveFailures_FirstAt { get; set; }
        public DateTime? ConsecutiveFailures_LastNotifiedAt { get; set; }
        public DateTime? ConsecutiveFailures_ResolvedAt { get; set; }

        /// <summary>True if any breach type is currently active for this tenant.</summary>
        public bool IsAnyTypeActive()
            => SuccessRate_IsActive
            || Duration_IsActive
            || AppInstall_IsActive
            || ConsecutiveFailures_IsActive;

        public static SlaTenantStatus CreateEmpty(string tenantId)
            => new() { TenantId = tenantId };
    }
}
