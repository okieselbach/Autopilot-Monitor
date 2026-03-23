using System.Net;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Galactic;

/// <summary>
/// API endpoints for managing persistent Galactic Admin notifications.
/// All endpoints require GalacticAdminOnly authorization (enforced by PolicyEnforcementMiddleware).
/// </summary>
public class GalacticNotificationsFunction
{
    private readonly ILogger<GalacticNotificationsFunction> _logger;
    private readonly GalacticNotificationService _notificationService;

    public GalacticNotificationsFunction(
        ILogger<GalacticNotificationsFunction> logger,
        GalacticNotificationService notificationService)
    {
        _logger = logger;
        _notificationService = notificationService;
    }

    /// <summary>
    /// GET /api/galactic/notifications
    /// Returns all active (non-dismissed) notifications, newest first.
    /// </summary>
    [Function("GetGalacticNotifications")]
    public async Task<HttpResponseData> GetNotifications(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "galactic/notifications")] HttpRequestData req)
    {
        var notifications = await _notificationService.GetActiveNotificationsAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { success = true, notifications });
        return response;
    }

    /// <summary>
    /// POST /api/galactic/notifications/{notificationId}/dismiss
    /// Dismisses a single notification.
    /// </summary>
    [Function("DismissGalacticNotification")]
    public async Task<HttpResponseData> DismissNotification(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "galactic/notifications/{notificationId}/dismiss")] HttpRequestData req,
        string notificationId)
    {
        var found = await _notificationService.DismissNotificationAsync(notificationId);

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
    /// POST /api/galactic/notifications/dismiss-all
    /// Dismisses all active notifications.
    /// </summary>
    [Function("DismissAllGalacticNotifications")]
    public async Task<HttpResponseData> DismissAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "galactic/notifications/dismiss-all")] HttpRequestData req)
    {
        var dismissedCount = await _notificationService.DismissAllNotificationsAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { success = true, dismissedCount });
        return response;
    }
}
