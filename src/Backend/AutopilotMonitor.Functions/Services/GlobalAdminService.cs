using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Service for managing Global Admin permissions
/// Global Admins can access cross-tenant data and perform platform-wide operations
/// </summary>
public class GlobalAdminService
{
    private readonly IAdminRepository _adminRepo;
    private readonly IMemoryCache _cache;
    private readonly ILogger<GlobalAdminService> _logger;
    private readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(5);

    public GlobalAdminService(
        IAdminRepository adminRepo,
        IMemoryCache cache,
        ILogger<GlobalAdminService> logger)
    {
        _adminRepo = adminRepo;
        _cache = cache;
        _logger = logger;
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

        // Query via repository
        _logger.LogInformation($"Querying repository for Global Admin: {upn}");
        var result = await _adminRepo.IsGlobalAdminAsync(upn);

        _logger.LogInformation($"Global Admin check result: {upn} -> {result}");

        // Cache the result
        _cache.Set(cacheKey, result, _cacheDuration);

        return result;
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

        await _adminRepo.AddGlobalAdminAsync(upn, addedBy);

        // Invalidate cache
        _cache.Remove($"global-admin:{upn}");

        return new GlobalAdminEntity
        {
            PartitionKey = "GlobalAdmins",
            RowKey = upn,
            Upn = upn,
            IsEnabled = true,
            AddedDate = DateTime.UtcNow,
            AddedBy = addedBy
        };
    }

    /// <summary>
    /// Removes a user from Global Admins
    /// </summary>
    public async Task RemoveGlobalAdminAsync(string upn)
    {
        upn = upn.ToLowerInvariant();

        await _adminRepo.RemoveGlobalAdminAsync(upn);

        // Invalidate cache
        _cache.Remove($"global-admin:{upn}");
    }

    /// <summary>
    /// Disables (but does not delete) a Global Admin
    /// </summary>
    public async Task DisableGlobalAdminAsync(string upn)
    {
        upn = upn.ToLowerInvariant();

        await _adminRepo.DisableGlobalAdminAsync(upn);

        // Invalidate cache
        _cache.Remove($"global-admin:{upn}");
    }

    /// <summary>
    /// Gets all Global Admins
    /// </summary>
    public async Task<List<GlobalAdminEntity>> GetAllGlobalAdminsAsync()
    {
        var entries = await _adminRepo.GetAllGlobalAdminsAsync();

        return entries.Select(e => new GlobalAdminEntity
        {
            PartitionKey = "GlobalAdmins",
            RowKey = e.Upn,
            Upn = e.Upn,
            IsEnabled = e.IsEnabled,
            AddedDate = e.AddedAt,
            AddedBy = e.AddedBy
        }).ToList();
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
