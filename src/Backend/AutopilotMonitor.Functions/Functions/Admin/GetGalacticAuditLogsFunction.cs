using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Admin
{
    public class GetGalacticAuditLogsFunction
    {
        private readonly ILogger<GetGalacticAuditLogsFunction> _logger;
        private readonly TableStorageService _storageService;
        private readonly GalacticAdminService _galacticAdminService;

        public GetGalacticAuditLogsFunction(
            ILogger<GetGalacticAuditLogsFunction> logger,
            TableStorageService storageService,
            GalacticAdminService galacticAdminService)
        {
            _logger = logger;
            _storageService = storageService;
            _galacticAdminService = galacticAdminService;
        }

        [Function("GetGalacticAuditLogs")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "galactic/audit/logs")] HttpRequestData req)
        {
            _logger.LogInformation("GetGalacticAuditLogs function processing request (Galactic Admin Mode)");

            try
            {
                // Validate authentication
                if (!TenantHelper.IsAuthenticated(req))
                {
                    _logger.LogWarning("Unauthenticated GetGalacticAuditLogs attempt");
                    var unauthorizedResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                    await unauthorizedResponse.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Authentication required. Please provide a valid JWT token."
                    });
                    return unauthorizedResponse;
                }

                // Check if user is Galactic Admin
                var userEmail = TenantHelper.GetUserIdentifier(req);
                var isGalacticAdmin = await _galacticAdminService.IsGalacticAdminAsync(userEmail);

                if (!isGalacticAdmin)
                {
                    _logger.LogWarning($"Non-Galactic Admin user {userEmail} attempted to access GetGalacticAuditLogs");
                    var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
                    await forbiddenResponse.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Access denied. Galactic Admin role required."
                    });
                    return forbiddenResponse;
                }

                _logger.LogInformation($"Fetching all audit logs across all tenants (User: {userEmail})");

                var logs = await _storageService.GetAllAuditLogsAsync(maxResults: 100);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    count = logs.Count,
                    logs = logs
                });

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting galactic audit logs");

                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "Internal server error",
                    count = 0,
                    logs = Array.Empty<object>()
                });

                return errorResponse;
            }
        }
    }
}
