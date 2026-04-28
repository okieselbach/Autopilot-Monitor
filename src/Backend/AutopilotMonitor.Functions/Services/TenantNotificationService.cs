using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Service for managing tenant-scoped persistent in-app notifications.
/// Backed by <see cref="ITenantNotificationRepository"/>; all reads/writes are partitioned per
/// tenantId so cross-tenant access is impossible at the repository layer.
/// Designed to be called fire-and-forget from request handlers — failures are logged, never thrown.
/// </summary>
public class TenantNotificationService
{
    private readonly ITenantNotificationRepository _repo;
    private readonly ILogger<TenantNotificationService> _logger;

    public TenantNotificationService(
        ITenantNotificationRepository repo,
        ILogger<TenantNotificationService> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public virtual async Task CreateNotificationAsync(string tenantId, string type, string title, string message, string? href = null)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            _logger.LogWarning("CreateNotificationAsync called with empty tenantId — skipping ({Type}: {Title})", type, title);
            return;
        }

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
                CreatedBy = "system",
                IsDismissed = false
            };

            await _repo.AddNotificationAsync(tenantId, notification);
            _logger.LogInformation("Tenant notification created: tenant={TenantId} id={NotificationId} type={Type}",
                tenantId, notificationId, type);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create tenant notification (tenant={TenantId} type={Type}: {Title})", tenantId, type, title);
        }
    }

    public async Task<List<GlobalNotificationDto>> GetActiveNotificationsAsync(string tenantId)
    {
        try
        {
            var notifications = await _repo.GetNotificationsAsync(tenantId);

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
            _logger.LogError(ex, "Failed to retrieve tenant notifications for tenant {TenantId}", tenantId);
            return new List<GlobalNotificationDto>();
        }
    }

    public async Task<bool> DismissNotificationAsync(string tenantId, string notificationId, string dismissedBy)
    {
        try
        {
            return await _repo.DismissNotificationAsync(tenantId, notificationId, dismissedBy);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dismiss tenant notification (tenant={TenantId} id={NotificationId})",
                tenantId, notificationId);
            return false;
        }
    }

    public async Task<int> DismissAllNotificationsAsync(string tenantId)
    {
        try
        {
            return await _repo.DismissAllNotificationsAsync(tenantId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dismiss all tenant notifications for tenant {TenantId}", tenantId);
            return 0;
        }
    }
}
