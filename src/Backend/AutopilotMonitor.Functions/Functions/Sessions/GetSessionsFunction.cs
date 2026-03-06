using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions
{
    public class GetSessionsFunction
    {
        private readonly ILogger<GetSessionsFunction> _logger;
        private readonly TableStorageService _storageService;

        public GetSessionsFunction(ILogger<GetSessionsFunction> logger, TableStorageService storageService)
        {
            _logger = logger;
            _storageService = storageService;
        }

        [Function("GetSessions")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sessions")] HttpRequestData req)
        {
            _logger.LogInformation("GetSessions function processing request");

            try
            {
                // Validate authentication
                if (!TenantHelper.IsAuthenticated(req))
                {
                    _logger.LogWarning("Unauthenticated GetSessions attempt");
                    var unauthorizedResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                    await unauthorizedResponse.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Authentication required. Please provide a valid JWT token.",
                        count = 0,
                        sessions = Array.Empty<object>()
                    });
                    return unauthorizedResponse;
                }

                var tenantId = TenantHelper.GetTenantId(req);

                _logger.LogInformation($"Fetching sessions for tenant {tenantId}");

                // Get sessions from storage
                var sessions = await _storageService.GetSessionsAsync(tenantId, maxResults: 100);

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
                _logger.LogError(ex, "Error getting sessions");

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
