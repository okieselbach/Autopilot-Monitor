using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Pagination;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Repository for global notifications and session reports.
    /// Covers: GlobalNotifications, SessionReports tables.
    /// </summary>
    public interface INotificationRepository
    {
        // --- Global Notifications ---
        Task<bool> AddNotificationAsync(GlobalNotification notification);
        Task<List<GlobalNotification>> GetNotificationsAsync(int maxResults = 50);
        Task<bool> DismissNotificationAsync(string notificationId, string dismissedBy);
        Task<int> DismissAllNotificationsAsync();

        // --- Session Reports ---
        Task<bool> StoreSessionReportMetadataAsync(SessionReportMetadata metadata);

        /// <summary>
        /// Returns all session reports, newest-first. Optional <paramref name="tenantId"/>
        /// filters server-side. No row cap on the full-fetch path; for installations
        /// with very large report archives prefer <see cref="GetSessionReportsPageAsync"/>.
        /// </summary>
        Task<List<SessionReportMetadata>> GetSessionReportsAsync(string? tenantId = null);

        /// <summary>
        /// Reads a single page of session reports newest-first. Optional
        /// <paramref name="tenantId"/> applies server-side. The returned
        /// <see cref="RawPage{T}"/> carries the underlying store's opaque
        /// continuation; <c>null</c> when this page was the last.
        /// </summary>
        Task<RawPage<SessionReportMetadata>> GetSessionReportsPageAsync(
            string? tenantId, int pageSize, string? continuation);

        Task<SessionReportMetadata?> GetSessionReportAsync(string reportId);
        Task<bool> UpdateSessionReportAdminNoteAsync(string reportId, string adminNote);
    }

    public class GlobalNotification
    {
        public string NotificationId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string Type { get; set; } = "info";
        public string Severity { get; set; } = "info";
        public string? Href { get; set; }
        public DateTime CreatedAt { get; set; }
        public string CreatedBy { get; set; } = string.Empty;
        public bool IsDismissed { get; set; }
        public string? DismissedBy { get; set; }
        public DateTime? DismissedAt { get; set; }
    }
}
