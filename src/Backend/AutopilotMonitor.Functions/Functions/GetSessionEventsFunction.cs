using System.Net;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions
{
    public class GetSessionEventsFunction
    {
        private readonly ILogger<GetSessionEventsFunction> _logger;
        private readonly TableStorageService _storageService;

        public GetSessionEventsFunction(ILogger<GetSessionEventsFunction> logger, TableStorageService storageService)
        {
            _logger = logger;
            _storageService = storageService;
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
                // Get tenant ID from query parameter (for now, default to demo GUID)
                var tenantId = req.Query["tenantId"] ?? "deadbeef-dead-beef-dead-beefdeadbeef";

                // Get events from storage
                var events = await _storageService.GetSessionEventsAsync(tenantId, sessionId);

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
