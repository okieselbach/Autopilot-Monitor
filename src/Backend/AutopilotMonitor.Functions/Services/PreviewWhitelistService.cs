using AutopilotMonitor.Shared;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Service for managing the Private Preview tenant whitelist.
/// Tenants in this list are allowed full portal access; others see a waitlist page.
/// Temporary — remove after GA.
/// </summary>
public class PreviewWhitelistService
{
    private readonly TableServiceClient _tableServiceClient;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PreviewWhitelistService> _logger;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public PreviewWhitelistService(
        IConfiguration configuration,
        IMemoryCache cache,
        ILogger<PreviewWhitelistService> logger)
    {
        _cache = cache;
        _logger = logger;
        var connectionString = configuration["AzureTableStorageConnectionString"];
        _tableServiceClient = new TableServiceClient(connectionString);
        // Table is initialized centrally by TableInitializerService at startup
    }

    /// <summary>
    /// Checks whether a tenant is approved for Private Preview (cached).
    /// </summary>
    public async Task<bool> IsApprovedAsync(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return false;

        var cacheKey = $"preview:{tenantId}";
        if (_cache.TryGetValue<bool>(cacheKey, out var approved))
            return approved;

        try
        {
            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.PreviewWhitelist);
            var entity = await tableClient.GetEntityAsync<PreviewWhitelistEntity>(tenantId, "approved");
            var result = entity?.Value != null;

            _cache.Set(cacheKey, result, CacheDuration);
            return result;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            _cache.Set(cacheKey, false, CacheDuration);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking preview whitelist for tenant {TenantId}", tenantId);
            // Fail-closed: if we can't check, deny access
            return false;
        }
    }

    /// <summary>
    /// Approves a tenant for Private Preview access.
    /// </summary>
    public async Task ApproveAsync(string tenantId, string approvedBy)
    {
        var entity = new PreviewWhitelistEntity
        {
            PartitionKey = tenantId,
            RowKey = "approved",
            ApprovedAt = DateTime.UtcNow,
            ApprovedBy = approvedBy
        };

        var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.PreviewWhitelist);
        await tableClient.UpsertEntityAsync(entity);

        _cache.Remove($"preview:{tenantId}");
        _logger.LogInformation("Tenant {TenantId} approved for preview by {ApprovedBy}", tenantId, approvedBy);
    }

    /// <summary>
    /// Revokes a tenant's Private Preview access.
    /// </summary>
    public async Task RevokeAsync(string tenantId)
    {
        var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.PreviewWhitelist);
        await tableClient.DeleteEntityAsync(tenantId, "approved");

        _cache.Remove($"preview:{tenantId}");
        _logger.LogInformation("Tenant {TenantId} revoked from preview", tenantId);
    }

    /// <summary>
    /// Returns all approved tenants (for Galactic Admin overview).
    /// </summary>
    public async Task<List<PreviewWhitelistEntity>> GetAllApprovedAsync()
    {
        var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.PreviewWhitelist);
        var results = new List<PreviewWhitelistEntity>();

        await foreach (var entity in tableClient.QueryAsync<PreviewWhitelistEntity>(
            filter: "RowKey eq 'approved'"))
        {
            results.Add(entity);
        }

        return results.OrderBy(e => e.PartitionKey).ToList();
    }

    /// <summary>
    /// Gets the notification email for a tenant (stored in PreviewWhitelist table).
    /// </summary>
    public async Task<string?> GetNotificationEmailAsync(string tenantId)
    {
        try
        {
            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.PreviewWhitelist);
            var entity = await tableClient.GetEntityAsync<PreviewNotificationEntity>(tenantId, "notification-email");
            return entity?.Value?.Email;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read notification email for tenant {TenantId}", tenantId);
            return null;
        }
    }

    /// <summary>
    /// Saves (or clears) the notification email for a tenant.
    /// </summary>
    public async Task SaveNotificationEmailAsync(string tenantId, string? email)
    {
        var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.PreviewWhitelist);

        if (string.IsNullOrWhiteSpace(email))
        {
            // Clear: delete the row if it exists
            try { await tableClient.DeleteEntityAsync(tenantId, "notification-email"); }
            catch (RequestFailedException ex) when (ex.Status == 404) { /* already gone */ }
            return;
        }

        var entity = new PreviewNotificationEntity
        {
            PartitionKey = tenantId,
            RowKey = "notification-email",
            Email = email.Trim()
        };
        await tableClient.UpsertEntityAsync(entity);
    }
}

/// <summary>
/// Entity representing an approved tenant in the PreviewWhitelist table.
/// </summary>
public class PreviewWhitelistEntity : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty; // TenantId
    public string RowKey { get; set; } = "approved";
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public DateTime ApprovedAt { get; set; }
    public string ApprovedBy { get; set; } = string.Empty;
}

/// <summary>
/// Entity storing the notification email for a tenant in the PreviewWhitelist table.
/// PartitionKey = TenantId, RowKey = "notification-email".
/// Temporary — remove after GA.
/// </summary>
public class PreviewNotificationEntity : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty; // TenantId
    public string RowKey { get; set; } = "notification-email";
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string Email { get; set; } = string.Empty;
}
