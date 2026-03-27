using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Entity for persistent Global Admin notifications stored in Table Storage.
/// All GAs share the same notification pool; dismiss is global.
/// </summary>
public class GlobalNotificationEntity : ITableEntity
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
/// DTO returned by the API for global notifications.
/// </summary>
public class GlobalNotificationDto
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = "info";
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? Href { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Service for managing persistent Global Admin in-app notifications.
/// Notifications survive page reloads and persist until actively dismissed.
/// Delegates storage to INotificationRepository; keeps business logic (fire-and-forget, DTO conversion).
/// </summary>
public class GlobalNotificationService
{
    private readonly INotificationRepository _notificationRepo;
    private readonly ILogger<GlobalNotificationService> _logger;

    public GlobalNotificationService(
        INotificationRepository notificationRepo,
        ILogger<GlobalNotificationService> logger)
    {
        _notificationRepo = notificationRepo;
        _logger = logger;
    }

    /// <summary>
    /// Creates a new persistent notification. Designed to be called fire-and-forget;
    /// failures are logged but never thrown.
    /// </summary>
    public async Task CreateNotificationAsync(string type, string title, string message, string? href = null)
    {
        try
        {
            var notificationId = Guid.NewGuid().ToString("N")[..12];

            var notification = new GlobalNotification
            {
                NotificationId = notificationId,
                Type = type,
                Title = title,
                Message = message,
                Href = href,
                CreatedAt = DateTime.UtcNow,
                IsDismissed = false
            };

            await _notificationRepo.AddNotificationAsync(notification);
            _logger.LogInformation("Global notification created: {NotificationId} ({Type})", notificationId, type);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create global notification ({Type}: {Title})", type, title);
        }
    }

    /// <summary>
    /// Returns all active (non-dismissed) notifications, newest first.
    /// </summary>
    public async Task<List<GlobalNotificationDto>> GetActiveNotificationsAsync()
    {
        try
        {
            var notifications = await _notificationRepo.GetNotificationsAsync();

            return notifications.Select(n => new GlobalNotificationDto
            {
                Id = n.NotificationId,
                Type = n.Type,
                Title = n.Title,
                Message = n.Message,
                Href = n.Href,
                CreatedAt = n.CreatedAt
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve global notifications");
            return new List<GlobalNotificationDto>();
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
            return await _notificationRepo.DismissNotificationAsync(notificationId, string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dismiss global notification {NotificationId}", notificationId);
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
            return await _notificationRepo.DismissAllNotificationsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dismiss all global notifications");
            return 0;
        }
    }
}
