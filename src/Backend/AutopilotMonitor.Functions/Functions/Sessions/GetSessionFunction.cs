using System.Net;
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
                // Cross-tenant access check handled by middleware (TargetTenantId)
                var requestCtx = req.GetRequestContext();

                var session = await _sessionRepo.GetSessionAsync(requestCtx.TargetTenantId, sessionId);

                // Global Admin cross-tenant fallback: if not found in the effective tenant,
                // try resolving the actual tenant via SessionsIndex
                if (session == null && requestCtx.IsGlobalAdmin)
                {
                    var resolvedTenantId = await _sessionRepo.FindSessionTenantIdAsync(sessionId);
                    if (resolvedTenantId != null && !string.Equals(resolvedTenantId, requestCtx.TargetTenantId, StringComparison.OrdinalIgnoreCase))
                    {
                        session = await _sessionRepo.GetSessionAsync(resolvedTenantId, sessionId);
                    }
                }

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
