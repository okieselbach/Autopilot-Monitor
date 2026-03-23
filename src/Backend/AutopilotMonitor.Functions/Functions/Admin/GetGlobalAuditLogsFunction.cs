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

        public GetGalacticAuditLogsFunction(
            ILogger<GetGalacticAuditLogsFunction> logger,
            TableStorageService storageService)
        {
            _logger = logger;
            _storageService = storageService;
        }

        [Function("GetGalacticAuditLogs")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "galactic/audit/logs")] HttpRequestData req)
        {
            _logger.LogInformation("GetGalacticAuditLogs function processing request (Galactic Admin Mode)");

            try
            {
                // Authentication + GalacticAdminOnly authorization enforced by PolicyEnforcementMiddleware
                var userEmail = TenantHelper.GetUserIdentifier(req);

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
