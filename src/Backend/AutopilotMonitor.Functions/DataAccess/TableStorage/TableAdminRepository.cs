using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Table Storage implementation of IAdminRepository.
    /// Performs direct CRUD against GlobalAdmins and TenantAdmins tables.
    /// </summary>
    public class TableAdminRepository : IAdminRepository
    {
        private readonly TableClient _globalAdminsTableClient;
        private readonly TableClient _tenantAdminsTableClient;
        private readonly TableClient _mcpUsersTableClient;
        private readonly ILogger<TableAdminRepository> _logger;

        public TableAdminRepository(
            TableStorageService storage,
            ILogger<TableAdminRepository> logger)
        {
            _logger = logger;
            _globalAdminsTableClient = storage.GetTableClient(Constants.TableNames.GlobalAdmins);
            _tenantAdminsTableClient = storage.GetTableClient(Constants.TableNames.TenantAdmins);
            _mcpUsersTableClient = storage.GetTableClient(Constants.TableNames.McpUsers);
        }

        // --- Global Admins ---

        public async Task<bool> IsGlobalAdminAsync(string upn)
        {
            if (string.IsNullOrWhiteSpace(upn))
                return false;

            try
            {
                var normalizedUpn = upn.ToLowerInvariant();
                var entity = await _globalAdminsTableClient.GetEntityAsync<GlobalAdminEntity>(
                    "GlobalAdmins", normalizedUpn);
                return entity?.Value != null && entity.Value.IsEnabled;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking global admin status for {Upn}", upn);
                return false;
            }
        }

        public async Task<List<GlobalAdminEntry>> GetAllGlobalAdminsAsync()
        {
            var admins = new List<GlobalAdminEntry>();
            await foreach (var entity in _globalAdminsTableClient.QueryAsync<GlobalAdminEntity>(
                filter: $"PartitionKey eq 'GlobalAdmins'"))
            {
                admins.Add(new GlobalAdminEntry
                {
                    Upn = entity.Upn,
                    IsEnabled = entity.IsEnabled,
                    AddedAt = entity.AddedDate,
                    AddedBy = entity.AddedBy
                });
            }
            return admins;
        }

        public async Task<bool> AddGlobalAdminAsync(string upn, string addedBy)
        {
            upn = upn.ToLowerInvariant();
            addedBy = addedBy.ToLowerInvariant();

            var entity = new GlobalAdminEntity
            {
                PartitionKey = "GlobalAdmins",
                RowKey = upn,
                Upn = upn,
                IsEnabled = true,
                AddedDate = DateTime.UtcNow,
                AddedBy = addedBy
            };

            await _globalAdminsTableClient.UpsertEntityAsync(entity);
            return true;
        }

        public async Task<bool> RemoveGlobalAdminAsync(string upn)
        {
            upn = upn.ToLowerInvariant();
            await _globalAdminsTableClient.DeleteEntityAsync("GlobalAdmins", upn);
            return true;
        }

        public async Task<bool> DisableGlobalAdminAsync(string upn)
        {
            upn = upn.ToLowerInvariant();

            try
            {
                var result = await _globalAdminsTableClient.GetEntityAsync<GlobalAdminEntity>(
                    "GlobalAdmins", upn);
                var entity = result.Value;
                if (entity != null)
                {
                    entity.IsEnabled = false;
                    await _globalAdminsTableClient.UpdateEntityAsync(entity, ETag.All);
                }
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return false;
            }
        }

        // --- MCP Users ---

        public async Task<bool> IsMcpUserAsync(string upn)
        {
            if (string.IsNullOrWhiteSpace(upn))
                return false;

            try
            {
                var normalizedUpn = upn.ToLowerInvariant();
                var entity = await _mcpUsersTableClient.GetEntityAsync<McpUserEntity>(
                    "McpUsers", normalizedUpn);
                return entity?.Value != null && entity.Value.IsEnabled;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking MCP user status for {Upn}", upn);
                return false;
            }
        }

        public async Task<List<McpUserEntry>> GetAllMcpUsersAsync()
        {
            var users = new List<McpUserEntry>();
            await foreach (var entity in _mcpUsersTableClient.QueryAsync<McpUserEntity>(
                filter: $"PartitionKey eq 'McpUsers'"))
            {
                users.Add(new McpUserEntry
                {
                    Upn = entity.Upn,
                    IsEnabled = entity.IsEnabled,
                    AddedAt = entity.AddedDate,
                    AddedBy = entity.AddedBy
                });
            }
            return users;
        }

        public async Task<bool> AddMcpUserAsync(string upn, string addedBy)
        {
            upn = upn.ToLowerInvariant();
            addedBy = addedBy.ToLowerInvariant();

            var entity = new McpUserEntity
            {
                PartitionKey = "McpUsers",
                RowKey = upn,
                Upn = upn,
                IsEnabled = true,
                AddedDate = DateTime.UtcNow,
                AddedBy = addedBy
            };

            await _mcpUsersTableClient.UpsertEntityAsync(entity);
            return true;
        }

        public async Task<bool> RemoveMcpUserAsync(string upn)
        {
            upn = upn.ToLowerInvariant();
            await _mcpUsersTableClient.DeleteEntityAsync("McpUsers", upn);
            return true;
        }

        public async Task<bool> SetMcpUserEnabledAsync(string upn, bool isEnabled)
        {
            upn = upn.ToLowerInvariant();

            try
            {
                var result = await _mcpUsersTableClient.GetEntityAsync<McpUserEntity>(
                    "McpUsers", upn);
                var entity = result.Value;
                if (entity != null)
                {
                    entity.IsEnabled = isEnabled;
                    await _mcpUsersTableClient.UpdateEntityAsync(entity, ETag.All);
                }
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return false;
            }
        }

        // --- Tenant Members ---

        public async Task<List<TenantMember>> GetTenantMembersAsync(string tenantId)
        {
            tenantId = tenantId.ToLowerInvariant();
            var members = new List<TenantMember>();

            await foreach (var entity in _tenantAdminsTableClient.QueryAsync<TenantAdminEntity>(
                filter: $"PartitionKey eq '{tenantId}'"))
            {
                members.Add(MapToTenantMember(entity));
            }

            return members;
        }

        public async Task<bool> AddTenantMemberAsync(string tenantId, string upn, string addedBy, string role, bool canManageBootstrapTokens = false)
        {
            tenantId = tenantId.ToLowerInvariant();
            upn = upn.ToLowerInvariant();
            addedBy = addedBy.ToLowerInvariant();

            var entity = new TenantAdminEntity
            {
                PartitionKey = tenantId,
                RowKey = upn,
                TenantId = tenantId,
                Upn = upn,
                IsEnabled = true,
                AddedDate = DateTime.UtcNow,
                AddedBy = addedBy,
                Role = role,
                CanManageBootstrapTokens = canManageBootstrapTokens
            };

            await _tenantAdminsTableClient.UpsertEntityAsync(entity);
            return true;
        }

        public async Task<bool> RemoveTenantMemberAsync(string tenantId, string upn)
        {
            tenantId = tenantId.ToLowerInvariant();
            upn = upn.ToLowerInvariant();
            await _tenantAdminsTableClient.DeleteEntityAsync(tenantId, upn);
            return true;
        }

        public async Task<bool> UpdateMemberPermissionsAsync(string tenantId, string upn, string role, bool canManageBootstrapTokens)
        {
            tenantId = tenantId.ToLowerInvariant();
            upn = upn.ToLowerInvariant();

            try
            {
                var result = await _tenantAdminsTableClient.GetEntityAsync<TenantAdminEntity>(tenantId, upn);
                var entity = result.Value;
                if (entity == null) return false;

                entity.Role = role;
                entity.CanManageBootstrapTokens = canManageBootstrapTokens;
                await _tenantAdminsTableClient.UpdateEntityAsync(entity, ETag.All);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return false;
            }
        }

        public async Task<bool> SetTenantMemberEnabledAsync(string tenantId, string upn, bool isEnabled)
        {
            tenantId = tenantId.ToLowerInvariant();
            upn = upn.ToLowerInvariant();

            try
            {
                var result = await _tenantAdminsTableClient.GetEntityAsync<TenantAdminEntity>(tenantId, upn);
                var entity = result.Value;
                if (entity == null) return false;

                entity.IsEnabled = isEnabled;
                await _tenantAdminsTableClient.UpdateEntityAsync(entity, ETag.All);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return false;
            }
        }

        public async Task<TenantMember?> GetTenantMemberAsync(string tenantId, string upn)
        {
            tenantId = tenantId.ToLowerInvariant();
            upn = upn.ToLowerInvariant();

            try
            {
                var result = await _tenantAdminsTableClient.GetEntityAsync<TenantAdminEntity>(tenantId, upn);
                var entity = result.Value;
                if (entity == null) return null;
                return MapToTenantMember(entity);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting tenant member {Upn} for tenant {TenantId}", upn, tenantId);
                return null;
            }
        }

        public async Task<bool> IsTenantAdminAsync(string tenantId, string upn)
        {
            var member = await GetTenantMemberAsync(tenantId, upn);
            if (member == null || !member.IsEnabled) return false;
            // Only true for Admin role (null Role = Admin for backward compat)
            return member.Role == null || member.Role == Constants.TenantRoles.Admin;
        }

        public async Task<bool> IsTenantMemberAsync(string tenantId, string upn)
        {
            var member = await GetTenantMemberAsync(tenantId, upn);
            if (member == null || !member.IsEnabled) return false;
            return member.Role != Constants.TenantRoles.Viewer;
        }

        // --- Helpers ---

        private static TenantMember MapToTenantMember(TenantAdminEntity entity)
        {
            return new TenantMember
            {
                Upn = entity.Upn,
                TenantId = entity.TenantId,
                Role = entity.Role ?? Constants.TenantRoles.Admin,
                IsEnabled = entity.IsEnabled,
                CanManageBootstrapTokens = entity.CanManageBootstrapTokens,
                AddedAt = entity.AddedDate,
                AddedBy = entity.AddedBy
            };
        }

    }
}
