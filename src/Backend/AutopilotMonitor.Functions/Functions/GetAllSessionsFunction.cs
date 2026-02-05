using System.Net;
using AutopilotMonitor.Functions.Helpers;
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
        private readonly GalacticAdminService _galacticAdminService;

        public GetAllSessionsFunction(
            ILogger<GetAllSessionsFunction> logger,
            TableStorageService storageService,
            GalacticAdminService galacticAdminService)
        {
            _logger = logger;
            _storageService = storageService;
            _galacticAdminService = galacticAdminService;
        }

        [Function("GetAllSessions")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "galactic/sessions")] HttpRequestData req)
        {
            _logger.LogInformation("GetAllSessions function processing request (Galactic Admin Mode)");

            try
            {
                // Validate authentication and Galactic Admin role
                var httpContext = req.FunctionContext.GetHttpContext();
                if (httpContext?.User?.Identity?.IsAuthenticated != true)
                {
                    _logger.LogWarning("Unauthenticated GetAllSessions attempt");
                    var unauthorizedResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                    await unauthorizedResponse.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Authentication required. Please provide a valid JWT token."
                    });
                    return unauthorizedResponse;
                }

                // Check if user is Galactic Admin via GalacticAdminService (Azure Table Storage)
                var userEmail = TenantHelper.GetUserIdentifier(req);
                var isGalacticAdmin = await _galacticAdminService.IsGalacticAdminAsync(userEmail);

                if (!isGalacticAdmin)
                {
                    _logger.LogWarning($"Non-Galactic Admin user {userEmail} attempted to access GetAllSessions");
                    var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
                    await forbiddenResponse.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Access denied. Galactic Admin role required."
                    });
                    return forbiddenResponse;
                }

                _logger.LogInformation($"Fetching all sessions across all tenants (User: {userEmail})");

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
