using System.Net;
using System.Web;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Sessions
{
    public class GetSessionEventsFunction
    {
        private readonly ILogger<GetSessionEventsFunction> _logger;
        private readonly ISessionRepository _sessionRepo;

        public GetSessionEventsFunction(
            ILogger<GetSessionEventsFunction> logger,
            ISessionRepository sessionRepo)
        {
            _logger = logger;
            _sessionRepo = sessionRepo;
        }

        [Function("GetSessionEvents")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sessions/{sessionId}/events")] HttpRequestData req,
            string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequestResponse.WriteAsJsonAsync(new { success = false, message = "SessionId is required" });
                return badRequestResponse;
            }

            var sessionPrefix = $"[Session: {sessionId.Substring(0, Math.Min(8, sessionId.Length))}]";
            _logger.LogInformation($"{sessionPrefix} GetSessionEvents: Fetching events");

            try
            {
                // Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware

                // Get user's tenant ID and identifier from JWT
                var requestCtx = req.GetRequestContext();
                var userIdentifier = requestCtx.UserPrincipalName;

                // Get requested tenant ID from query parameter
                var query = HttpUtility.ParseQueryString(req.Url.Query);
                var requestedTenantId = query["tenantId"];

                if (string.IsNullOrEmpty(requestedTenantId))
                {
                    _logger.LogWarning($"{sessionPrefix} Missing tenantId query parameter");
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

                // Validate tenant access: user must either own the tenant or be Global Admin
                if (!requestCtx.IsGlobalAdmin && requestedTenantId != requestCtx.TenantId)
                {
                    _logger.LogWarning($"{sessionPrefix} User {userIdentifier} (tenant {requestCtx.TenantId}) attempted to access session events for tenant {requestedTenantId}");
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
                else if (requestCtx.IsGlobalAdmin && requestedTenantId != requestCtx.TenantId)
                {
                    _logger.LogInformation($"{sessionPrefix} Global Admin {userIdentifier} accessing cross-tenant session events (tenant: {requestedTenantId})");
                }

                // Get events from storage using the requested tenant ID
                var events = await _sessionRepo.GetSessionEventsAsync(requestedTenantId, sessionId);

                return await req.OkAsync(new
                {
                    success = true,
                    sessionId = sessionId,
                    count = events.Count,
                    events = events
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting events for session {sessionId}");

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
}
