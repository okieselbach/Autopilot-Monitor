using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Service for managing Tenant Admin permissions
/// Tenant Admins have full access to their specific tenant's configuration, sessions, and diagnostics
/// </summary>
public class TenantAdminsService
{
    private readonly TableServiceClient _tableServiceClient;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TenantAdminsService> _logger;
    private readonly string _tableName = "TenantAdmins";
    private readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(5);

    public TenantAdminsService(
        IConfiguration configuration,
        IMemoryCache cache,
        ILogger<TenantAdminsService> logger)
    {
        _cache = cache;
        _logger = logger;
        var connectionString = configuration["AzureTableStorageConnectionString"];
        _tableServiceClient = new TableServiceClient(connectionString);

        // Ensure table exists
        InitializeTableAsync().GetAwaiter().GetResult();
    }

    private async Task InitializeTableAsync()
    {
        try
        {
            await _tableServiceClient.CreateTableIfNotExistsAsync(_tableName);
            _logger.LogInformation($"Tenant Admins table initialized: {_tableName}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to initialize Tenant Admins table: {_tableName}");
        }
    }

    /// <summary>
    /// Checks if a UPN is a Tenant Admin for a specific tenant
    /// Uses caching for performance
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    /// <param name="upn">User Principal Name (e.g., oliver@contoso.com)</param>
    /// <returns>True if the user is a Tenant Admin for this tenant</returns>
    public async Task<bool> IsTenantAdminAsync(string tenantId, string? upn)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(upn))
        {
            _logger.LogDebug("IsTenantAdminAsync: TenantId or UPN is null or empty");
            return false;
        }

        // Normalize for case-insensitive comparison
        tenantId = tenantId.ToLowerInvariant();
        upn = upn.ToLowerInvariant();
        _logger.LogInformation($"Checking if user is Tenant Admin: {upn} for tenant {tenantId}");

        // Check cache first
        var cacheKey = $"tenant-admin:{tenantId}:{upn}";
        if (_cache.TryGetValue<bool>(cacheKey, out var isAdmin))
        {
            _logger.LogInformation($"Tenant Admin check (from cache): {upn} @ {tenantId} -> {isAdmin}");
            return isAdmin;
        }

        // Query Table Storage
        _logger.LogInformation($"Querying Table Storage for Tenant Admin: {upn} @ {tenantId}");
        var admin = await GetTenantAdminAsync(tenantId, upn);
        var result = admin != null && admin.IsEnabled;

        _logger.LogInformation($"Tenant Admin check result: {upn} @ {tenantId} -> {result} (Entity found: {admin != null}, IsEnabled: {admin?.IsEnabled})");

        // Cache the result
        _cache.Set(cacheKey, result, _cacheDuration);

        return result;
    }

    /// <summary>
    /// Gets a Tenant Admin entity by tenant ID and UPN
    /// </summary>
    private async Task<TenantAdminEntity?> GetTenantAdminAsync(string tenantId, string upn)
    {
        try
        {
            var tableClient = _tableServiceClient.GetTableClient(_tableName);
            var normalizedTenantId = tenantId.ToLowerInvariant();
            var normalizedUpn = upn.ToLowerInvariant();

            _logger.LogDebug($"Querying Table Storage - PartitionKey: '{normalizedTenantId}', RowKey: '{normalizedUpn}'");

            // PartitionKey = TenantId, RowKey = UPN
            var entity = await tableClient.GetEntityAsync<TenantAdminEntity>(
                normalizedTenantId,
                normalizedUpn
            );

            _logger.LogDebug($"Tenant Admin entity found for {normalizedUpn} @ {normalizedTenantId}");
            return entity?.Value;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            // Entity not found
            _logger.LogDebug($"Tenant Admin entity NOT found for {upn} @ {tenantId} (404)");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error querying Tenant Admin for {upn} @ {tenantId}");
            return null;
        }
    }

    /// <summary>
    /// Adds a user as a Tenant Admin
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    /// <param name="upn">User Principal Name</param>
    /// <param name="addedBy">UPN of the admin who is adding this user</param>
    public async Task<TenantAdminEntity> AddTenantAdminAsync(string tenantId, string upn, string addedBy)
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
            AddedBy = addedBy
        };

        var tableClient = _tableServiceClient.GetTableClient(_tableName);
        await tableClient.UpsertEntityAsync(entity);

        // Invalidate cache
        _cache.Remove($"tenant-admin:{tenantId}:{upn}");

        _logger.LogInformation($"Added Tenant Admin: {upn} to tenant {tenantId} by {addedBy}");

        return entity;
    }

    /// <summary>
    /// Removes a user from Tenant Admins
    /// </summary>
    public async Task RemoveTenantAdminAsync(string tenantId, string upn)
    {
        tenantId = tenantId.ToLowerInvariant();
        upn = upn.ToLowerInvariant();

        var tableClient = _tableServiceClient.GetTableClient(_tableName);
        await tableClient.DeleteEntityAsync(tenantId, upn);

        // Invalidate cache
        _cache.Remove($"tenant-admin:{tenantId}:{upn}");

        _logger.LogInformation($"Removed Tenant Admin: {upn} from tenant {tenantId}");
    }

    /// <summary>
    /// Disables (but does not delete) a Tenant Admin
    /// </summary>
    public async Task DisableTenantAdminAsync(string tenantId, string upn)
    {
        tenantId = tenantId.ToLowerInvariant();
        upn = upn.ToLowerInvariant();

        var admin = await GetTenantAdminAsync(tenantId, upn);
        if (admin != null)
        {
            admin.IsEnabled = false;

            var tableClient = _tableServiceClient.GetTableClient(_tableName);
            await tableClient.UpdateEntityAsync(admin, Azure.ETag.All);

            // Invalidate cache
            _cache.Remove($"tenant-admin:{tenantId}:{upn}");

            _logger.LogInformation($"Disabled Tenant Admin: {upn} for tenant {tenantId}");
        }
    }

    /// <summary>
    /// Enables a Tenant Admin
    /// </summary>
    public async Task EnableTenantAdminAsync(string tenantId, string upn)
    {
        tenantId = tenantId.ToLowerInvariant();
        upn = upn.ToLowerInvariant();

        var admin = await GetTenantAdminAsync(tenantId, upn);
        if (admin != null)
        {
            admin.IsEnabled = true;

            var tableClient = _tableServiceClient.GetTableClient(_tableName);
            await tableClient.UpdateEntityAsync(admin, Azure.ETag.All);

            // Invalidate cache
            _cache.Remove($"tenant-admin:{tenantId}:{upn}");

            _logger.LogInformation($"Enabled Tenant Admin: {upn} for tenant {tenantId}");
        }
    }

    /// <summary>
    /// Gets all Tenant Admins for a specific tenant
    /// </summary>
    public async Task<List<TenantAdminEntity>> GetTenantAdminsAsync(string tenantId)
    {
        tenantId = tenantId.ToLowerInvariant();
        var tableClient = _tableServiceClient.GetTableClient(_tableName);

        var admins = new List<TenantAdminEntity>();
        await foreach (var entity in tableClient.QueryAsync<TenantAdminEntity>(
            filter: $"PartitionKey eq '{tenantId}'"))
        {
            admins.Add(entity);
        }

        _logger.LogInformation($"Retrieved {admins.Count} Tenant Admins for tenant {tenantId}");

        return admins;
    }

    /// <summary>
    /// Clears the cache for a specific tenant's admins
    /// Useful after bulk updates
    /// </summary>
    public void ClearCacheForTenant(string tenantId)
    {
        // Note: IMemoryCache doesn't have a clear all method by default
        // In production, consider using a distributed cache with better cache invalidation
        // For now, cache entries will expire after _cacheDuration
    }
}

/// <summary>
/// Entity representing a Tenant Admin in Table Storage
/// </summary>
public class TenantAdminEntity : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty; // TenantId (lowercase)
    public string RowKey { get; set; } = string.Empty; // UPN in lowercase
    public DateTimeOffset? Timestamp { get; set; }
    public Azure.ETag ETag { get; set; }

    /// <summary>
    /// Tenant ID (lowercase)
    /// </summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// User Principal Name (lowercase)
    /// </summary>
    public string Upn { get; set; } = string.Empty;

    /// <summary>
    /// Whether this admin is currently enabled
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// When this admin was added
    /// </summary>
    public DateTime AddedDate { get; set; }

    /// <summary>
    /// UPN of the admin who added this user
    /// </summary>
    public string AddedBy { get; set; } = string.Empty;
}
