using AutopilotMonitor.Shared;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Service for managing Global Admin permissions
/// Global Admins can access cross-tenant data and perform platform-wide operations
/// </summary>
public class GlobalAdminService
{
    private readonly TableServiceClient _tableServiceClient;
    private readonly IMemoryCache _cache;
    private readonly ILogger<GlobalAdminService> _logger;
    private readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(5);

    public GlobalAdminService(
        IConfiguration configuration,
        IMemoryCache cache,
        ILogger<GlobalAdminService> logger)
    {
        _cache = cache;
        _logger = logger;
        var connectionString = configuration["AzureTableStorageConnectionString"];
        _tableServiceClient = new TableServiceClient(connectionString);
        // Table is initialized centrally by TableInitializerService at startup
    }

    /// <summary>
    /// Checks if a UPN is a Global Admin
    /// Uses caching for performance
    /// </summary>
    /// <param name="upn">User Principal Name (e.g., oliver@contoso.com)</param>
    /// <returns>True if the user is a Global Admin</returns>
    public async Task<bool> IsGlobalAdminAsync(string? upn)
    {
        if (string.IsNullOrWhiteSpace(upn))
        {
            _logger.LogDebug("IsGlobalAdminAsync: UPN is null or empty");
            return false;
        }

        // Normalize UPN to lowercase for case-insensitive comparison
        upn = upn.ToLowerInvariant();
        _logger.LogInformation($"Checking if user is Global Admin: {upn}");

        // Check cache first
        var cacheKey = $"global-admin:{upn}";
        if (_cache.TryGetValue<bool>(cacheKey, out var isAdmin))
        {
            _logger.LogInformation($"Global Admin check (from cache): {upn} -> {isAdmin}");
            return isAdmin;
        }

        // Query Table Storage
        _logger.LogInformation($"Querying Table Storage for Global Admin: {upn}");
        var admin = await GetGlobalAdminAsync(upn);
        var result = admin != null && admin.IsEnabled;

        _logger.LogInformation($"Global Admin check result: {upn} -> {result} (Entity found: {admin != null}, IsEnabled: {admin?.IsEnabled})");

        // Cache the result
        _cache.Set(cacheKey, result, _cacheDuration);

        return result;
    }

    /// <summary>
    /// Gets a Global Admin entity by UPN
    /// </summary>
    private async Task<GlobalAdminEntity?> GetGlobalAdminAsync(string upn)
    {
        try
        {
            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.GlobalAdmins);
            var normalizedUpn = upn.ToLowerInvariant();

            _logger.LogDebug($"Querying Table Storage - PartitionKey: 'GlobalAdmins', RowKey: '{normalizedUpn}'");

            // PartitionKey = "GlobalAdmins", RowKey = UPN
            var entity = await tableClient.GetEntityAsync<GlobalAdminEntity>(
                "GlobalAdmins",
                normalizedUpn
            );

            _logger.LogDebug($"Global Admin entity found for {normalizedUpn}");
            return entity?.Value;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            // Entity not found
            _logger.LogDebug($"Global Admin entity NOT found for {upn} (404)");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error querying Global Admin for {upn}");
            return null;
        }
    }

    /// <summary>
    /// Adds a user as a Global Admin
    /// </summary>
    /// <param name="upn">User Principal Name</param>
    /// <param name="addedBy">UPN of the admin who is adding this user</param>
    public async Task<GlobalAdminEntity> AddGlobalAdminAsync(string upn, string addedBy)
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

        var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.GlobalAdmins);
        await tableClient.UpsertEntityAsync(entity);

        // Invalidate cache
        _cache.Remove($"global-admin:{upn}");

        return entity;
    }

    /// <summary>
    /// Removes a user from Global Admins
    /// </summary>
    public async Task RemoveGlobalAdminAsync(string upn)
    {
        upn = upn.ToLowerInvariant();

        var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.GlobalAdmins);
        await tableClient.DeleteEntityAsync("GlobalAdmins", upn);

        // Invalidate cache
        _cache.Remove($"global-admin:{upn}");
    }

    /// <summary>
    /// Disables (but does not delete) a Global Admin
    /// </summary>
    public async Task DisableGlobalAdminAsync(string upn)
    {
        upn = upn.ToLowerInvariant();

        var admin = await GetGlobalAdminAsync(upn);
        if (admin != null)
        {
            admin.IsEnabled = false;

            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.GlobalAdmins);
            await tableClient.UpdateEntityAsync(admin, Azure.ETag.All);

            // Invalidate cache
            _cache.Remove($"global-admin:{upn}");
        }
    }

    /// <summary>
    /// Gets all Global Admins
    /// </summary>
    public async Task<List<GlobalAdminEntity>> GetAllGlobalAdminsAsync()
    {
        var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.GlobalAdmins);

        var admins = new List<GlobalAdminEntity>();
        await foreach (var entity in tableClient.QueryAsync<GlobalAdminEntity>(
            filter: $"PartitionKey eq 'GlobalAdmins'"))
        {
            admins.Add(entity);
        }

        return admins;
    }

    /// <summary>
    /// Clears the cache for all Global Admins
    /// Useful after bulk updates
    /// </summary>
    public void ClearCache()
    {
        // Note: IMemoryCache doesn't have a clear all method by default
        // In production, consider using a distributed cache with better cache invalidation
        // For now, cache entries will expire after _cacheDuration
    }
}

/// <summary>
/// Entity representing a Global Admin in Table Storage
/// </summary>
public class GlobalAdminEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "GlobalAdmins";
    public string RowKey { get; set; } = string.Empty; // UPN in lowercase
    public DateTimeOffset? Timestamp { get; set; }
    public Azure.ETag ETag { get; set; }

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
