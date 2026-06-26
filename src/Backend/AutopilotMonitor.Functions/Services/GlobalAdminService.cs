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
    // Per-process cache: on scaled-out Flex Consumption, the _cache.Remove on add/remove/disable
    // only clears the mutating instance, so other instances serve a stale global role until expiry.
    // A short TTL caps that cross-instance window so a granted/revoked GlobalAdmin or GlobalReader
    // role self-heals in seconds. The lookup is a single Table Storage point-read. Do NOT raise this
    // back to minutes "for performance" — it reintroduces the role flip-flop (see TenantAdminsService).
    private readonly TimeSpan _cacheDuration = TimeSpan.FromSeconds(30);

    // Sentinel stored in the role cache to represent "no global role" (row missing or disabled).
    // Lets us distinguish a cached negative from a cache miss without nullable boxing games.
    private const string NoRoleSentinel = "(none)";

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
    public virtual async Task<bool> IsGlobalAdminAsync(string? upn)
    {
        // Single source of truth: GlobalAdmin == the GlobalAdmin platform role.
        return await GetGlobalRoleAsync(upn) == Constants.GlobalRoles.GlobalAdmin;
    }

    /// <summary>
    /// Resolves the caller's platform role from the GlobalAdmins table.
    /// Returns <see cref="Constants.GlobalRoles.GlobalAdmin"/>, <see cref="Constants.GlobalRoles.GlobalReader"/>,
    /// or <c>null</c> when the UPN has no enabled GlobalAdmins row. Cached briefly (see _cacheDuration).
    /// </summary>
    public virtual async Task<string?> GetGlobalRoleAsync(string? upn)
    {
        if (string.IsNullOrWhiteSpace(upn))
        {
            _logger.LogDebug("GetGlobalRoleAsync: UPN is null or empty");
            return null;
        }

        // Normalize UPN to lowercase for case-insensitive comparison
        upn = upn.ToLowerInvariant();

        var cacheKey = $"global-role:{upn}";
        if (_cache.TryGetValue<string>(cacheKey, out var cached) && cached != null)
        {
            _logger.LogDebug("Global role check (from cache): {Upn} -> {Role}", upn, cached);
            return cached == NoRoleSentinel ? null : cached;
        }

        _logger.LogDebug("Querying repository for global role: {Upn}", upn);
        var role = await _adminRepo.GetGlobalRoleAsync(upn);

        _logger.LogDebug("Global role check result: {Upn} -> {Role}", upn, role ?? "(none)");

        _cache.Set(cacheKey, role ?? NoRoleSentinel, _cacheDuration);

        return role;
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
        _cache.Remove($"global-role:{upn}");

        return new GlobalAdminEntity
        {
            PartitionKey = "GlobalAdmins",
            RowKey = upn,
            Upn = upn,
            IsEnabled = true,
            AddedDate = DateTime.UtcNow,
            AddedBy = addedBy,
            Role = Constants.GlobalRoles.GlobalAdmin
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
        _cache.Remove($"global-role:{upn}");
    }

    /// <summary>
    /// Disables (but does not delete) a Global Admin
    /// </summary>
    public async Task DisableGlobalAdminAsync(string upn)
    {
        upn = upn.ToLowerInvariant();

        await _adminRepo.DisableGlobalAdminAsync(upn);

        // Invalidate cache
        _cache.Remove($"global-role:{upn}");
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
            AddedBy = e.AddedBy,
            Role = string.IsNullOrEmpty(e.Role) ? Constants.GlobalRoles.GlobalAdmin : e.Role
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

    /// <summary>
    /// Platform role for this entry: <see cref="Constants.GlobalRoles.GlobalAdmin"/> (default) or
    /// <see cref="Constants.GlobalRoles.GlobalReader"/>. Empty/missing ⇒ GlobalAdmin (back-compat with
    /// rows created before the GlobalReader tier existed).
    /// </summary>
    public string Role { get; set; } = string.Empty;
}
