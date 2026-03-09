using AutopilotMonitor.Shared;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Entity for persistent Galactic Admin notifications stored in Table Storage.
/// All GAs share the same notification pool; dismiss is global.
/// </summary>
public class GalacticNotificationEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "notifications";
    public string RowKey { get; set; } = string.Empty; // invertedTicks_notificationId (newest-first)
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string NotificationId { get; set; } = string.Empty;
    public string Type { get; set; } = "info"; // "session_report" | "preview_signup"
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? Href { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool Dismissed { get; set; }
}

/// <summary>
/// DTO returned by the API for galactic notifications.
/// </summary>
public class GalacticNotificationDto
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = "info";
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? Href { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Service for managing persistent Galactic Admin in-app notifications.
/// Notifications survive page reloads and persist until actively dismissed.
/// </summary>
public class GalacticNotificationService
{
    private readonly TableServiceClient _tableServiceClient;
    private readonly ILogger<GalacticNotificationService> _logger;

    public GalacticNotificationService(
        IConfiguration configuration,
        ILogger<GalacticNotificationService> logger)
    {
        _logger = logger;
        var connectionString = configuration["AzureTableStorageConnectionString"];
        _tableServiceClient = new TableServiceClient(connectionString);
    }

    /// <summary>
    /// Creates a new persistent notification. Designed to be called fire-and-forget;
    /// failures are logged but never thrown.
    /// </summary>
    public async Task CreateNotificationAsync(string type, string title, string message, string? href = null)
    {
        try
        {
            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.GalacticNotifications);
            var notificationId = Guid.NewGuid().ToString("N")[..12];
            var invertedTicks = (DateTime.MaxValue.Ticks - DateTime.UtcNow.Ticks).ToString("D19");

            var entity = new GalacticNotificationEntity
            {
                PartitionKey = "notifications",
                RowKey = $"{invertedTicks}_{notificationId}",
                NotificationId = notificationId,
                Type = type,
                Title = title,
                Message = message,
                Href = href,
                CreatedAt = DateTime.UtcNow,
                Dismissed = false
            };

            await tableClient.UpsertEntityAsync(entity);
            _logger.LogInformation("Galactic notification created: {NotificationId} ({Type})", notificationId, type);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create galactic notification ({Type}: {Title})", type, title);
        }
    }

    /// <summary>
    /// Returns all active (non-dismissed) notifications, newest first.
    /// </summary>
    public async Task<List<GalacticNotificationDto>> GetActiveNotificationsAsync()
    {
        try
        {
            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.GalacticNotifications);
            var notifications = new List<GalacticNotificationDto>();

            await foreach (var entity in tableClient.QueryAsync<GalacticNotificationEntity>(
                e => e.PartitionKey == "notifications" && !e.Dismissed))
            {
                notifications.Add(new GalacticNotificationDto
                {
                    Id = entity.NotificationId,
                    Type = entity.Type,
                    Title = entity.Title,
                    Message = entity.Message,
                    Href = entity.Href,
                    CreatedAt = entity.CreatedAt
                });
            }

            return notifications;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogWarning("GalacticNotifications table not found — returning empty list");
            return new List<GalacticNotificationDto>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve galactic notifications");
            return new List<GalacticNotificationDto>();
        }
    }

    /// <summary>
    /// Marks a single notification as dismissed.
    /// Returns true if found and dismissed, false if not found.
    /// </summary>
    public async Task<bool> DismissNotificationAsync(string notificationId)
    {
        try
        {
            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.GalacticNotifications);

            await foreach (var entity in tableClient.QueryAsync<GalacticNotificationEntity>(
                e => e.PartitionKey == "notifications" && e.NotificationId == notificationId))
            {
                entity.Dismissed = true;
                await tableClient.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace);
                _logger.LogInformation("Galactic notification dismissed: {NotificationId}", notificationId);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dismiss galactic notification {NotificationId}", notificationId);
            return false;
        }
    }

    /// <summary>
    /// Dismisses all active notifications. Returns the count of dismissed items.
    /// </summary>
    public async Task<int> DismissAllNotificationsAsync()
    {
        try
        {
            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.GalacticNotifications);
            var count = 0;

            await foreach (var entity in tableClient.QueryAsync<GalacticNotificationEntity>(
                e => e.PartitionKey == "notifications" && !e.Dismissed))
            {
                entity.Dismissed = true;
                await tableClient.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace);
                count++;
            }

            _logger.LogInformation("Dismissed {Count} galactic notifications", count);
            return count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dismiss all galactic notifications");
            return 0;
        }
    }
}
