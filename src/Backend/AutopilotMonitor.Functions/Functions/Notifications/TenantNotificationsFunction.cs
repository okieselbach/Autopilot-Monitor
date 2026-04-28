using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Notifications;

/// <summary>
/// API endpoints for tenant-scoped persistent in-app notifications.
/// All endpoints resolve TenantId strictly from the JWT (TenantScoping.Jwt) so a tenant
/// can never read or dismiss another tenant's notifications.
/// Authorization is enforced by PolicyEnforcementMiddleware via EndpointAccessPolicyCatalog.
/// </summary>
public class TenantNotificationsFunction
{
    private readonly ILogger<TenantNotificationsFunction> _logger;
    private readonly TenantNotificationService _notificationService;

    public TenantNotificationsFunction(
        ILogger<TenantNotificationsFunction> logger,
        TenantNotificationService notificationService)
    {
        _logger = logger;
        _notificationService = notificationService;
    }

    /// <summary>
    /// GET /api/notifications
    /// Returns all active (non-dismissed) notifications for the caller's tenant, newest first.
    /// </summary>
    [Function("GetTenantNotifications")]
    public async Task<HttpResponseData> GetNotifications(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "notifications")] HttpRequestData req)
    {
        var tenantId = TenantHelper.GetTenantId(req);
        var notifications = await _notificationService.GetActiveNotificationsAsync(tenantId);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { success = true, notifications });
        return response;
    }

    /// <summary>
    /// POST /api/notifications/{notificationId}/dismiss
    /// Dismisses a single notification within the caller's tenant scope.
    /// </summary>
    [Function("DismissTenantNotification")]
    public async Task<HttpResponseData> DismissNotification(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "notifications/{notificationId}/dismiss")] HttpRequestData req,
        string notificationId)
    {
        var tenantId = TenantHelper.GetTenantId(req);
        var dismissedBy = TenantHelper.GetUserIdentifier(req);
        var found = await _notificationService.DismissNotificationAsync(tenantId, notificationId, dismissedBy);

        if (!found)
        {
            var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
            await notFoundResponse.WriteAsJsonAsync(new { success = false, message = "Notification not found" });
            return notFoundResponse;
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { success = true });
        return response;
    }

    /// <summary>
    /// POST /api/notifications/dismiss-all
    /// Dismisses all active notifications within the caller's tenant scope.
    /// </summary>
    [Function("DismissAllTenantNotifications")]
    public async Task<HttpResponseData> DismissAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "notifications/dismiss-all")] HttpRequestData req)
    {
        var tenantId = TenantHelper.GetTenantId(req);
        var dismissedCount = await _notificationService.DismissAllNotificationsAsync(tenantId);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { success = true, dismissedCount });
        return response;
    }
}
