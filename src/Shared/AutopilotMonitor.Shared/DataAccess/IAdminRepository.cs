using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Repository for admin and tenant member management.
    /// Covers: GlobalAdmins, TenantAdmins, ApiKeys tables.
    /// </summary>
    public interface IAdminRepository
    {
        // --- Global Admins ---
        Task<bool> IsGlobalAdminAsync(string upn);
        Task<List<GlobalAdminEntry>> GetAllGlobalAdminsAsync();
        Task<bool> AddGlobalAdminAsync(string upn, string addedBy);
        Task<bool> RemoveGlobalAdminAsync(string upn);
        Task<bool> DisableGlobalAdminAsync(string upn);

        // --- Tenant Members ---
        Task<List<TenantMember>> GetTenantMembersAsync(string tenantId);
        Task<bool> AddTenantMemberAsync(string tenantId, string upn, string addedBy, string role, bool canManageBootstrapTokens = false);
        Task<bool> RemoveTenantMemberAsync(string tenantId, string upn);
        Task<bool> UpdateMemberPermissionsAsync(string tenantId, string upn, string role, bool canManageBootstrapTokens);
        Task<bool> SetTenantMemberEnabledAsync(string tenantId, string upn, bool isEnabled);
        Task<TenantMember?> GetTenantMemberAsync(string tenantId, string upn);
        Task<bool> IsTenantAdminAsync(string tenantId, string upn);
        Task<bool> IsTenantMemberAsync(string tenantId, string upn);

        // --- API Keys ---
        Task<ApiKeyEntry?> ValidateApiKeyAsync(string apiKeyHash);
        Task<bool> StoreApiKeyAsync(string tenantId, ApiKeyEntry entry);
        Task<List<ApiKeyEntry>> GetApiKeysAsync(string tenantId);
        Task<List<ApiKeyEntry>> GetAllApiKeysAsync();
        Task<ApiKeyEntry?> GetApiKeyAsync(string partitionKey, string keyId);
        Task<bool> RevokeApiKeyAsync(string tenantId, string keyId);
        Task IncrementApiKeyRequestCountAsync(string partitionKey, string keyId);
    }

    public class GlobalAdminEntry
    {
        public string Upn { get; set; } = string.Empty;
        public bool IsEnabled { get; set; } = true;
        public DateTime AddedAt { get; set; }
        public string AddedBy { get; set; } = string.Empty;
    }

    public class TenantMember
    {
        public string Upn { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public string Role { get; set; } = "Admin";
        public bool IsEnabled { get; set; } = true;
        public bool CanManageBootstrapTokens { get; set; }
        public DateTime AddedAt { get; set; }
        public string AddedBy { get; set; } = string.Empty;
    }

    public class ApiKeyEntry
    {
        public string KeyId { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public string KeyHash { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Scope { get; set; } = "tenant";
        public string Upn { get; set; } = string.Empty;
        public string CreatedBy { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public bool IsActive { get; set; } = true;
        public long RequestCount { get; set; }
    }
}
