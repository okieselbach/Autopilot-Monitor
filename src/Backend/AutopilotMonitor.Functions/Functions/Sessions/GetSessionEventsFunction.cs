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
