using System.Net;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions
{
    public class GetAllSessionsFunction
    {
        private readonly ILogger<GetAllSessionsFunction> _logger;
        private readonly TableStorageService _storageService;

        public GetAllSessionsFunction(ILogger<GetAllSessionsFunction> logger, TableStorageService storageService)
        {
            _logger = logger;
            _storageService = storageService;
        }

        [Function("GetAllSessions")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "galactic/sessions")] HttpRequestData req)
        {
            _logger.LogInformation("GetAllSessions function processing request (Galactic Admin Mode)");

            try
            {
                _logger.LogInformation("Fetching all sessions across all tenants");

                // Get all sessions from storage (no tenant filter)
                var sessions = await _storageService.GetAllSessionsAsync(maxResults: 100);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    count = sessions.Count,
                    sessions = sessions
                });

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting all sessions");

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
    }
}
