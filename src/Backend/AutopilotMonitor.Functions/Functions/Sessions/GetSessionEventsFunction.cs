using System.Net;
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
                // Cross-tenant access check handled by middleware (TargetTenantId)
                var requestCtx = req.GetRequestContext();

                // Get events from storage using the resolved tenant ID
                var events = await _sessionRepo.GetSessionEventsAsync(requestCtx.TargetTenantId, sessionId);

                // Global Admin cross-tenant fallback: if no events found in the effective tenant,
                // try resolving the actual tenant via SessionsIndex
                if (events.Count == 0 && requestCtx.IsGlobalAdmin)
                {
                    var resolvedTenantId = await _sessionRepo.FindSessionTenantIdAsync(sessionId);
                    if (resolvedTenantId != null && !string.Equals(resolvedTenantId, requestCtx.TargetTenantId, StringComparison.OrdinalIgnoreCase))
                    {
                        events = await _sessionRepo.GetSessionEventsAsync(resolvedTenantId, sessionId);
                    }
                }

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
                return await req.InternalServerErrorAsync(_logger, ex, $"Get events for session '{sessionId}'");
            }
        }
    }
}
