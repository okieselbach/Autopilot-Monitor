using System.Net;
using System.Web;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Sessions
{
    public class GetSessionFunction
    {
        private readonly ILogger<GetSessionFunction> _logger;
        private readonly ISessionRepository _sessionRepo;

        public GetSessionFunction(
            ILogger<GetSessionFunction> logger,
            ISessionRepository sessionRepo)
        {
            _logger = logger;
            _sessionRepo = sessionRepo;
        }

        [Function("GetSession")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sessions/{sessionId}")] HttpRequestData req,
            string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "SessionId is required"
                });
                return badRequest;
            }

            var sessionPrefix = $"[Session: {sessionId.Substring(0, Math.Min(8, sessionId.Length))}]";

            try
            {
                // Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware
                var requestCtx = req.GetRequestContext();
                var userIdentifier = requestCtx.UserPrincipalName;
                var query = HttpUtility.ParseQueryString(req.Url.Query);
                var requestedTenantId = query["tenantId"];
                var effectiveTenantId = string.IsNullOrWhiteSpace(requestedTenantId) ? requestCtx.TenantId : requestedTenantId;

                if (string.IsNullOrWhiteSpace(effectiveTenantId))
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Unable to resolve tenantId."
                    });
                    return badRequest;
                }

                if (!requestCtx.IsGlobalAdmin && !string.Equals(effectiveTenantId, requestCtx.TenantId, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning($"{sessionPrefix} User {userIdentifier} (tenant {requestCtx.TenantId}) attempted to access session in tenant {effectiveTenantId}");
                    var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
                    await forbiddenResponse.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Access denied. You can only view sessions for your own tenant."
                    });
                    return forbiddenResponse;
                }

                var session = await _sessionRepo.GetSessionAsync(effectiveTenantId, sessionId);
                if (session == null)
                {
                    var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFound.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Session not found",
                        sessionId
                    });
                    return notFound;
                }

                return await req.OkAsync(new
                {
                    success = true,
                    session
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"{sessionPrefix} Error getting session");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "Internal server error"
                });
                return errorResponse;
            }
        }
    }
}
