using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Service for managing Galactic Admin permissions
/// Galactic Admins can access cross-tenant data and perform platform-wide operations
/// </summary>
public class GalacticAdminService
{
    private readonly TableServiceClient _tableServiceClient;
    private readonly IMemoryCache _cache;
    private readonly ILogger<GalacticAdminService> _logger;
    private readonly string _tableName = "GalacticAdmins";
    private readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(5);

    public GalacticAdminService(
        IConfiguration configuration,
        IMemoryCache cache,
        ILogger<GalacticAdminService> logger)
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
            _logger.LogInformation($"Galactic Admins table initialized: {_tableName}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to initialize Galactic Admins table: {_tableName}");
        }
    }

    /// <summary>
    /// Checks if a UPN is a Galactic Admin
    /// Uses caching for performance
    /// </summary>
    /// <param name="upn">User Principal Name (e.g., oliver@contoso.com)</param>
    /// <returns>True if the user is a Galactic Admin</returns>
    public async Task<bool> IsGalacticAdminAsync(string? upn)
    {
        if (string.IsNullOrWhiteSpace(upn))
        {
            _logger.LogDebug("IsGalacticAdminAsync: UPN is null or empty");
            return false;
        }

        // Normalize UPN to lowercase for case-insensitive comparison
        upn = upn.ToLowerInvariant();
        _logger.LogInformation($"Checking if user is Galactic Admin: {upn}");

        // Check cache first
        var cacheKey = $"galactic-admin:{upn}";
        if (_cache.TryGetValue<bool>(cacheKey, out var isAdmin))
        {
            _logger.LogInformation($"Galactic Admin check (from cache): {upn} -> {isAdmin}");
            return isAdmin;
        }

        // Query Table Storage
        _logger.LogInformation($"Querying Table Storage for Galactic Admin: {upn}");
        var admin = await GetGalacticAdminAsync(upn);
        var result = admin != null && admin.IsEnabled;

        _logger.LogInformation($"Galactic Admin check result: {upn} -> {result} (Entity found: {admin != null}, IsEnabled: {admin?.IsEnabled})");

        // Cache the result
        _cache.Set(cacheKey, result, _cacheDuration);

        return result;
    }

    /// <summary>
    /// Gets a Galactic Admin entity by UPN
    /// </summary>
    private async Task<GalacticAdminEntity?> GetGalacticAdminAsync(string upn)
    {
        try
        {
            var tableClient = _tableServiceClient.GetTableClient(_tableName);
            var normalizedUpn = upn.ToLowerInvariant();

            _logger.LogDebug($"Querying Table Storage - PartitionKey: 'GalacticAdmins', RowKey: '{normalizedUpn}'");

            // PartitionKey = "GalacticAdmins", RowKey = UPN
            var entity = await tableClient.GetEntityAsync<GalacticAdminEntity>(
                "GalacticAdmins",
                normalizedUpn
            );

            _logger.LogDebug($"Galactic Admin entity found for {normalizedUpn}");
            return entity?.Value;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            // Entity not found
            _logger.LogDebug($"Galactic Admin entity NOT found for {upn} (404)");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error querying Galactic Admin for {upn}");
            return null;
        }
    }

    /// <summary>
    /// Adds a user as a Galactic Admin
    /// </summary>
    /// <param name="upn">User Principal Name</param>
    /// <param name="addedBy">UPN of the admin who is adding this user</param>
    public async Task<GalacticAdminEntity> AddGalacticAdminAsync(string upn, string addedBy)
    {
        upn = upn.ToLowerInvariant();
        addedBy = addedBy.ToLowerInvariant();

        var entity = new GalacticAdminEntity
        {
            PartitionKey = "GalacticAdmins",
            RowKey = upn,
            Upn = upn,
            IsEnabled = true,
            AddedDate = DateTime.UtcNow,
            AddedBy = addedBy
        };

        var tableClient = _tableServiceClient.GetTableClient(_tableName);
        await tableClient.UpsertEntityAsync(entity);

        // Invalidate cache
        _cache.Remove($"galactic-admin:{upn}");

        return entity;
    }

    /// <summary>
    /// Removes a user from Galactic Admins
    /// </summary>
    public async Task RemoveGalacticAdminAsync(string upn)
    {
        upn = upn.ToLowerInvariant();

        var tableClient = _tableServiceClient.GetTableClient(_tableName);
        await tableClient.DeleteEntityAsync("GalacticAdmins", upn);

        // Invalidate cache
        _cache.Remove($"galactic-admin:{upn}");
    }

    /// <summary>
    /// Disables (but does not delete) a Galactic Admin
    /// </summary>
    public async Task DisableGalacticAdminAsync(string upn)
    {
        upn = upn.ToLowerInvariant();

        var admin = await GetGalacticAdminAsync(upn);
        if (admin != null)
        {
            admin.IsEnabled = false;

            var tableClient = _tableServiceClient.GetTableClient(_tableName);
            await tableClient.UpdateEntityAsync(admin, Azure.ETag.All);

            // Invalidate cache
            _cache.Remove($"galactic-admin:{upn}");
        }
    }

    /// <summary>
    /// Gets all Galactic Admins
    /// </summary>
    public async Task<List<GalacticAdminEntity>> GetAllGalacticAdminsAsync()
    {
        var tableClient = _tableServiceClient.GetTableClient(_tableName);

        var admins = new List<GalacticAdminEntity>();
        await foreach (var entity in tableClient.QueryAsync<GalacticAdminEntity>(
            filter: $"PartitionKey eq 'GalacticAdmins'"))
        {
            admins.Add(entity);
        }

        return admins;
    }

    /// <summary>
    /// Clears the cache for all Galactic Admins
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
/// Entity representing a Galactic Admin in Table Storage
/// </summary>
public class GalacticAdminEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "GalacticAdmins";
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
