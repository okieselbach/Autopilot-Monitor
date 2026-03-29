using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Repository for admin and tenant member management.
    /// Covers: GlobalAdmins, TenantAdmins tables.
    /// </summary>
    public interface IAdminRepository
    {
        // --- Global Admins ---
        Task<bool> IsGlobalAdminAsync(string upn);
        Task<List<GlobalAdminEntry>> GetAllGlobalAdminsAsync();
        Task<bool> AddGlobalAdminAsync(string upn, string addedBy);
        Task<bool> RemoveGlobalAdminAsync(string upn);
        Task<bool> DisableGlobalAdminAsync(string upn);

        // --- MCP Users ---
        Task<bool> IsMcpUserAsync(string upn);
        Task<McpUserEntry?> GetMcpUserAsync(string upn);
        Task<List<McpUserEntry>> GetAllMcpUsersAsync();
        Task<bool> AddMcpUserAsync(string upn, string addedBy);
        Task<bool> RemoveMcpUserAsync(string upn);
        Task<bool> SetMcpUserEnabledAsync(string upn, bool isEnabled);
        Task<bool> SetMcpUserUsagePlanAsync(string upn, string? usagePlan);

        // --- Tenant Members ---
        Task<List<TenantMember>> GetTenantMembersAsync(string tenantId);
        Task<bool> AddTenantMemberAsync(string tenantId, string upn, string addedBy, string role, bool canManageBootstrapTokens = false);
        Task<bool> RemoveTenantMemberAsync(string tenantId, string upn);
        Task<bool> UpdateMemberPermissionsAsync(string tenantId, string upn, string role, bool canManageBootstrapTokens);
        Task<bool> SetTenantMemberEnabledAsync(string tenantId, string upn, bool isEnabled);
        Task<TenantMember?> GetTenantMemberAsync(string tenantId, string upn);
        Task<bool> IsTenantAdminAsync(string tenantId, string upn);
        Task<bool> IsTenantMemberAsync(string tenantId, string upn);
    }

    public class GlobalAdminEntry
    {
        public string Upn { get; set; } = string.Empty;
        public bool IsEnabled { get; set; } = true;
        public DateTime AddedAt { get; set; }
        public string AddedBy { get; set; } = string.Empty;
    }

    public class McpUserEntry
    {
        public string Upn { get; set; } = string.Empty;
        public bool IsEnabled { get; set; } = true;
        public DateTime AddedAt { get; set; }
        public string AddedBy { get; set; } = string.Empty;
        public string? UsagePlan { get; set; }
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

}
