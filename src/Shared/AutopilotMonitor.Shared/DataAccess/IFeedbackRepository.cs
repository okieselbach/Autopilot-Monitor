using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// User-feedback storage. Backed by the dedicated <c>Feedback</c> Azure-Table which is
    /// intentionally NOT in any tenant-offboarding wipe list — feedback (especially from
    /// offboarded tenants) is exactly the data we want to keep for product learning.
    /// <para>
    /// Two partition layouts share the table:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>PK="InApp"</c>, <c>RK=upn</c> — in-app star rating + comment from the
    ///   feedback bubble. One row per user (Upsert-replaces on resubmit).</item>
    ///   <item><c>PK="Offboarding"</c>, <c>RK=historyRowKey</c> — free-form "what could we
    ///   improve" comment captured during the offboarding drain-barrier countdown. One row
    ///   per offboarding attempt (matches the <c>OffboardingHistory</c> row).</item>
    /// </list>
    /// </summary>
    public interface IFeedbackRepository
    {
        // ── In-App feedback (star rating + comment) ─────────────────────────────

        /// <summary>Returns the in-app feedback entry for the given UPN, or null if the user has not interacted.</summary>
        Task<FeedbackEntry?> GetInAppFeedbackAsync(string upn);

        /// <summary>Upserts the in-app feedback entry for the given UPN. Sets <see cref="FeedbackEntry.Type"/> to <c>"InApp"</c>.</summary>
        Task SaveInAppFeedbackAsync(FeedbackEntry entry);

        // ── Offboarding feedback (one per offboarding history row) ──────────────

        /// <summary>Returns the offboarding feedback for the given history-row-key, or null if none submitted.</summary>
        Task<FeedbackEntry?> GetOffboardingFeedbackAsync(string historyRowKey);

        /// <summary>Upserts the offboarding feedback entry. Sets <see cref="FeedbackEntry.Type"/> to <c>"Offboarding"</c>.</summary>
        Task SaveOffboardingFeedbackAsync(FeedbackEntry entry);

        // ── Reports / dashboard ─────────────────────────────────────────────────

        /// <summary>Returns ALL feedback entries (both In-App + Offboarding partitions). Used by the Global-Admin reports page.</summary>
        Task<List<FeedbackEntry>> GetAllAsync();
    }

    /// <summary>
    /// Single shape for both partitions; nullable fields disambiguate the two kinds.
    /// </summary>
    public class FeedbackEntry
    {
        /// <summary><c>"InApp"</c> or <c>"Offboarding"</c>. Matches the storage PartitionKey.</summary>
        public string Type { get; set; } = FeedbackEntryType.InApp;

        public string Upn { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? Comment { get; set; }
        public DateTime? InteractedAt { get; set; }

        // ── In-App-only ─────────────────────────────────────────────────────────
        public int? Rating { get; set; }
        public bool Dismissed { get; set; }
        public bool Submitted { get; set; }

        // ── Offboarding-only ────────────────────────────────────────────────────

        /// <summary>RowKey of the matching <c>OffboardingHistory</c> entry. Only set for <c>Type="Offboarding"</c>.</summary>
        public string? HistoryRowKey { get; set; }

        /// <summary>Snapshot of the tenant's domain at offboarding time — TenantConfiguration is wiped in Phase 2 so this captures the display value once. Only set for <c>Type="Offboarding"</c>.</summary>
        public string? DomainName { get; set; }
    }

    /// <summary>Discriminator values for <see cref="FeedbackEntry.Type"/> + storage PartitionKey.</summary>
    public static class FeedbackEntryType
    {
        public const string InApp = "InApp";
        public const string Offboarding = "Offboarding";
    }
}
