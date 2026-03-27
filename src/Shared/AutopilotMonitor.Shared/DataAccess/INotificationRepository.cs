using AutopilotMonitor.Shared.Models;

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

        // --- Session Reports ---
        Task<bool> StoreSessionReportMetadataAsync(SessionReportMetadata metadata);
        Task<List<SessionReportMetadata>> GetSessionReportsAsync(string? tenantId = null, int maxResults = 50);
        Task<SessionReportMetadata?> GetSessionReportAsync(string reportId);
    }

    public class GlobalNotification
    {
        public string NotificationId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string Severity { get; set; } = "info";
        public DateTime CreatedAt { get; set; }
        public string CreatedBy { get; set; } = string.Empty;
        public bool IsDismissed { get; set; }
        public string? DismissedBy { get; set; }
        public DateTime? DismissedAt { get; set; }
    }
}
