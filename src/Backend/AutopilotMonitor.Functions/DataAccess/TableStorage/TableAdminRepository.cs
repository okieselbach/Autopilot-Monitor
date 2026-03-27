using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Table Storage implementation of IAdminRepository.
    /// Delegates GlobalAdmin/TenantMember operations to existing services,
    /// and implements API Key operations directly against the ApiKeys table.
    /// </summary>
    public class TableAdminRepository : IAdminRepository
    {
        private readonly GlobalAdminService _globalAdminService;
        private readonly TenantAdminsService _tenantAdminsService;
        private readonly TableClient _apiKeysTableClient;
        private readonly ILogger<TableAdminRepository> _logger;

        public TableAdminRepository(
            GlobalAdminService globalAdminService,
            TenantAdminsService tenantAdminsService,
            IConfiguration configuration,
            ILogger<TableAdminRepository> logger)
        {
            _globalAdminService = globalAdminService;
            _tenantAdminsService = tenantAdminsService;
            _logger = logger;

            var connectionString = configuration["AzureTableStorageConnectionString"];
            var serviceClient = new TableServiceClient(connectionString);
            _apiKeysTableClient = serviceClient.GetTableClient(Constants.TableNames.ApiKeys);
        }

        // --- Global Admins (delegate to existing service) ---

        public Task<bool> IsGlobalAdminAsync(string upn)
            => _globalAdminService.IsGlobalAdminAsync(upn);

        public async Task<List<GlobalAdminEntry>> GetAllGlobalAdminsAsync()
        {
            var entities = await _globalAdminService.GetAllGlobalAdminsAsync();
            return entities.Select(e => new GlobalAdminEntry
            {
                Upn = e.Upn,
                IsEnabled = e.IsEnabled,
                AddedAt = e.AddedDate,
                AddedBy = e.AddedBy
            }).ToList();
        }

        public async Task<bool> AddGlobalAdminAsync(string upn, string addedBy)
        {
            await _globalAdminService.AddGlobalAdminAsync(upn, addedBy);
            return true;
        }

        public async Task<bool> RemoveGlobalAdminAsync(string upn)
        {
            await _globalAdminService.RemoveGlobalAdminAsync(upn);
            return true;
        }

        public async Task<bool> DisableGlobalAdminAsync(string upn)
        {
            await _globalAdminService.DisableGlobalAdminAsync(upn);
            return true;
        }

        // --- Tenant Members (delegate to existing service) ---

        public async Task<List<TenantMember>> GetTenantMembersAsync(string tenantId)
        {
            var entities = await _tenantAdminsService.GetTenantAdminsAsync(tenantId);
            return entities.Select(e => new TenantMember
            {
                Upn = e.Upn,
                TenantId = e.TenantId,
                Role = e.Role ?? "Admin",
                IsEnabled = e.IsEnabled,
                CanManageBootstrapTokens = e.CanManageBootstrapTokens,
                AddedAt = e.AddedDate,
                AddedBy = e.AddedBy
            }).ToList();
        }

        public async Task<bool> AddTenantMemberAsync(string tenantId, string upn, string addedBy, string role, bool canManageBootstrapTokens = false)
        {
            await _tenantAdminsService.AddTenantMemberAsync(tenantId, upn, addedBy, role, canManageBootstrapTokens);
            return true;
        }

        public async Task<bool> RemoveTenantMemberAsync(string tenantId, string upn)
        {
            await _tenantAdminsService.RemoveTenantAdminAsync(tenantId, upn);
            return true;
        }

        public Task<bool> UpdateMemberPermissionsAsync(string tenantId, string upn, string role, bool canManageBootstrapTokens)
            => _tenantAdminsService.UpdateMemberPermissionsAsync(tenantId, upn, role, canManageBootstrapTokens);

        public async Task<TenantMember?> GetTenantMemberAsync(string tenantId, string upn)
        {
            var role = await _tenantAdminsService.GetMemberRoleAsync(tenantId, upn);
            if (role == null) return null;

            return new TenantMember
            {
                Upn = upn.ToLowerInvariant(),
                TenantId = tenantId.ToLowerInvariant(),
                Role = role.Role,
                IsEnabled = true,
                CanManageBootstrapTokens = role.CanManageBootstrapTokens
            };
        }

        public Task<bool> IsTenantAdminAsync(string tenantId, string upn)
            => _tenantAdminsService.IsTenantAdminAsync(tenantId, upn);

        public Task<bool> IsTenantMemberAsync(string tenantId, string upn)
            => _tenantAdminsService.IsTenantMemberAsync(tenantId, upn);

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
