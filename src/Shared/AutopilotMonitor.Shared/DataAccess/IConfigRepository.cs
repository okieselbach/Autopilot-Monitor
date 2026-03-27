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
    }
}
