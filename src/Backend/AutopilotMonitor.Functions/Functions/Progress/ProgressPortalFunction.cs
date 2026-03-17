using System.Net;
using System.Web;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Progress;

/// <summary>
/// Dedicated Progress Portal endpoints.
/// These routes are exempt from MemberAuthorizationMiddleware so any authenticated tenant user
/// can monitor enrollment sessions without needing an Admin or Operator role.
/// </summary>
public class ProgressPortalFunction
{
    private readonly ILogger<ProgressPortalFunction> _logger;
    private readonly TableStorageService _storageService;
    private readonly GalacticAdminService _galacticAdminService;

    public ProgressPortalFunction(
        ILogger<ProgressPortalFunction> logger,
        TableStorageService storageService,
        GalacticAdminService galacticAdminService)
    {
        _logger = logger;
        _storageService = storageService;
        _galacticAdminService = galacticAdminService;
    }

    /// <summary>
    /// GET /api/progress/sessions
    /// Returns sessions for the authenticated user's tenant.
    /// </summary>
    [Function("ProgressGetSessions")]
    public async Task<HttpResponseData> GetSessions(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "progress/sessions")] HttpRequestData req)
    {
        _logger.LogInformation("ProgressGetSessions processing request");

        try
        {
            // Authentication + AuthenticatedUser authorization enforced by PolicyEnforcementMiddleware
            var tenantId = TenantHelper.GetTenantId(req);

            _logger.LogInformation("ProgressGetSessions: Fetching sessions for tenant {TenantId}", tenantId);

            var page = await _storageService.GetSessionsAsync(tenantId, maxResults: 100);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                count = page.Sessions.Count,
                sessions = page.Sessions
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ProgressGetSessions");

            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new
            {
                success = false,
                message = "Internal server error",
                count = 0,
                sessions = Array.Empty<object>()
            });
            return errorResponse;
        }
    }

    /// <summary>
    /// GET /api/progress/sessions/{sessionId}/events
    /// Returns events for a specific session. Cross-tenant access only for Galactic Admins.
    /// </summary>
    [Function("ProgressGetSessionEvents")]
    public async Task<HttpResponseData> GetSessionEvents(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "progress/sessions/{sessionId}/events")] HttpRequestData req,
        string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequestResponse.WriteAsJsonAsync(new { success = false, message = "SessionId is required" });
            return badRequestResponse;
        }

        var sessionPrefix = $"[Session: {sessionId.Substring(0, Math.Min(8, sessionId.Length))}]";
        _logger.LogInformation("{SessionPrefix} ProgressGetSessionEvents: Fetching events", sessionPrefix);

        try
        {
            // Authentication + AuthenticatedUser authorization enforced by PolicyEnforcementMiddleware
            var userTenantId = TenantHelper.GetTenantId(req);
            var userIdentifier = TenantHelper.GetUserIdentifier(req);

            var query = HttpUtility.ParseQueryString(req.Url.Query);
            var requestedTenantId = query["tenantId"];

            if (string.IsNullOrEmpty(requestedTenantId))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "tenantId query parameter is required",
                    sessionId = sessionId,
                    count = 0,
                    events = Array.Empty<object>()
                });
                return badRequest;
            }

            // Cross-tenant access only for Galactic Admins
            if (requestedTenantId != userTenantId)
            {
                var isGalacticAdmin = await _galacticAdminService.IsGalacticAdminAsync(userIdentifier);
                if (!isGalacticAdmin)
                {
                    var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
                    await forbiddenResponse.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Access denied. You can only view events for sessions in your own tenant.",
                        sessionId = sessionId,
                        count = 0,
                        events = Array.Empty<object>()
                    });
                    return forbiddenResponse;
                }

                _logger.LogInformation("{SessionPrefix} Galactic Admin {User} accessing cross-tenant progress events (tenant: {TenantId})",
                    sessionPrefix, userIdentifier, requestedTenantId);
            }

            var events = await _storageService.GetSessionEventsAsync(requestedTenantId, sessionId);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                sessionId = sessionId,
                count = events.Count,
                events = events
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ProgressGetSessionEvents for session {SessionId}", sessionId);

            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new
            {
                success = false,
                message = "Internal server error",
                sessionId = sessionId,
                count = 0,
                events = Array.Empty<object>()
            });
            return errorResponse;
        }
    }
}
