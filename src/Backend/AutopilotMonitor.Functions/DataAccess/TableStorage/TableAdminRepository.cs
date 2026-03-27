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
    /// Performs direct CRUD against GlobalAdmins, TenantAdmins, and ApiKeys tables.
    /// </summary>
    public class TableAdminRepository : IAdminRepository
    {
        private readonly TableClient _globalAdminsTableClient;
        private readonly TableClient _tenantAdminsTableClient;
        private readonly TableClient _apiKeysTableClient;
        private readonly ILogger<TableAdminRepository> _logger;

        public TableAdminRepository(
            TableStorageService storage,
            ILogger<TableAdminRepository> logger)
        {
            _logger = logger;
            _globalAdminsTableClient = storage.GetTableClient(Constants.TableNames.GlobalAdmins);
            _tenantAdminsTableClient = storage.GetTableClient(Constants.TableNames.TenantAdmins);
            _apiKeysTableClient = storage.GetTableClient(Constants.TableNames.ApiKeys);
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

        // --- API Keys ---

        public async Task<ApiKeyEntry?> ValidateApiKeyAsync(string apiKeyHash)
        {
            await foreach (var entity in _apiKeysTableClient.QueryAsync<TableEntity>(
                filter: $"KeyHash eq '{apiKeyHash}'"))
            {
                return MapEntityToApiKeyEntry(entity);
            }
            return null;
        }

        public async Task<bool> StoreApiKeyAsync(string tenantId, ApiKeyEntry entry)
        {
            var entity = new TableEntity(tenantId, entry.KeyId)
            {
                ["KeyHash"] = entry.KeyHash,
                ["Label"] = entry.Name,
                ["Scope"] = entry.Scope,
                ["TenantId"] = entry.TenantId,
                ["CreatedBy"] = entry.CreatedBy,
                ["CreatedAt"] = entry.CreatedAt,
                ["IsActive"] = entry.IsActive,
                ["RequestCount"] = entry.RequestCount,
            };

            if (entry.ExpiresAt.HasValue)
                entity["ExpiresAt"] = new DateTimeOffset(entry.ExpiresAt.Value, TimeSpan.Zero);

            await _apiKeysTableClient.UpsertEntityAsync(entity);
            return true;
        }

        public async Task<List<ApiKeyEntry>> GetApiKeysAsync(string tenantId)
        {
            var keys = new List<ApiKeyEntry>();
            await foreach (var entity in _apiKeysTableClient.QueryAsync<TableEntity>(
                filter: $"PartitionKey eq '{tenantId}'"))
            {
                keys.Add(MapEntityToApiKeyEntry(entity));
            }
            return keys;
        }

        public async Task<List<ApiKeyEntry>> GetAllApiKeysAsync()
        {
            var keys = new List<ApiKeyEntry>();
            await foreach (var entity in _apiKeysTableClient.QueryAsync<TableEntity>())
            {
                keys.Add(MapEntityToApiKeyEntry(entity));
            }
            return keys;
        }

        public async Task<ApiKeyEntry?> GetApiKeyAsync(string partitionKey, string keyId)
        {
            try
            {
                var result = await _apiKeysTableClient.GetEntityAsync<TableEntity>(partitionKey, keyId);
                return MapEntityToApiKeyEntry(result.Value);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
        }

        public async Task<bool> RevokeApiKeyAsync(string tenantId, string keyId)
        {
            try
            {
                await _apiKeysTableClient.DeleteEntityAsync(tenantId, keyId);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return false;
            }
        }

        public async Task IncrementApiKeyRequestCountAsync(string partitionKey, string keyId)
        {
            try
            {
                var result = await _apiKeysTableClient.GetEntityAsync<TableEntity>(partitionKey, keyId);
                var entity = result.Value;
                var count = entity.TryGetValue("RequestCount", out var c) ? Convert.ToInt64(c) : 0L;
                entity["RequestCount"] = count + 1;
                await _apiKeysTableClient.UpdateEntityAsync(entity, entity.ETag);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to increment request count for key {KeyId}", keyId);
            }
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

        private static ApiKeyEntry MapEntityToApiKeyEntry(TableEntity entity)
        {
            return new ApiKeyEntry
            {
                KeyId = entity.RowKey,
                TenantId = entity.GetString("TenantId") ?? entity.PartitionKey,
                KeyHash = entity.GetString("KeyHash") ?? string.Empty,
                Name = entity.GetString("Label") ?? string.Empty,
                Scope = entity.GetString("Scope") ?? "tenant",
                CreatedBy = entity.GetString("CreatedBy") ?? string.Empty,
                CreatedAt = entity.GetDateTimeOffset("CreatedAt")?.UtcDateTime ?? DateTime.MinValue,
                ExpiresAt = entity.GetDateTimeOffset("ExpiresAt")?.UtcDateTime,
                IsActive = entity.GetBoolean("IsActive") ?? true,
                RequestCount = entity.TryGetValue("RequestCount", out var rc) ? Convert.ToInt64(rc) : 0L,
            };
        }
    }
}
