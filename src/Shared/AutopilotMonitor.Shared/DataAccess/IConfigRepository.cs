using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Repository for tenant and admin configuration.
    /// Covers: TenantConfiguration, AdminConfiguration, PreviewWhitelist, PreviewConfig tables.
    /// </summary>
    public interface IConfigRepository
    {
        // --- Tenant Configuration ---
        Task<TenantConfiguration?> GetTenantConfigurationAsync(string tenantId);
        Task<bool> SaveTenantConfigurationAsync(TenantConfiguration config);
        Task<List<TenantConfiguration>> GetAllTenantConfigurationsAsync();

        // --- Admin Configuration ---
        Task<AdminConfiguration?> GetAdminConfigurationAsync();
        Task<bool> SaveAdminConfigurationAsync(AdminConfiguration config);

        // --- Preview Whitelist ---
        Task<bool> IsInPreviewWhitelistAsync(string tenantId);
        Task<bool> AddToPreviewWhitelistAsync(string tenantId, string addedBy);
        Task<bool> RemoveFromPreviewWhitelistAsync(string tenantId);
        Task<List<string>> GetPreviewWhitelistAsync();

        // --- Preview Config ---
        Task<Dictionary<string, string>> GetPreviewConfigAsync();
        Task<bool> SavePreviewConfigAsync(string key, string value);

        // --- Preview Notification Email ---
        Task<string?> GetNotificationEmailAsync(string tenantId);
        Task SaveNotificationEmailAsync(string tenantId, string? email);

        // --- Feedback (stored in PreviewConfig table, PK="Feedback") ---
        Task<FeedbackEntry?> GetFeedbackEntryAsync(string upn);
        Task SaveFeedbackEntryAsync(FeedbackEntry entry);
        Task<List<FeedbackEntry>> GetAllFeedbackEntriesAsync();
    }

    public class FeedbackEntry
    {
        public string Upn { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public int? Rating { get; set; }
        public string? Comment { get; set; }
        public bool Dismissed { get; set; }
        public bool Submitted { get; set; }
        public DateTime? InteractedAt { get; set; }
    }
}
