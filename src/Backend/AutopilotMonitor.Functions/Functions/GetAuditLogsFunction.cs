using System.Net;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions
{
    public class GetAuditLogsFunction
    {
        private readonly ILogger<GetAuditLogsFunction> _logger;
        private readonly TableStorageService _storageService;

        public GetAuditLogsFunction(ILogger<GetAuditLogsFunction> logger, TableStorageService storageService)
        {
            _logger = logger;
            _storageService = storageService;
        }

        [Function("GetAuditLogs")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "audit/logs")] HttpRequestData req)
        {
            _logger.LogInformation("GetAuditLogs function processing request");

            try
            {
                // Get tenant ID from query parameter (for now, default to demo GUID)
                var tenantId = req.Query["tenantId"] ?? "deadbeef-dead-beef-dead-beefdeadbeef";

                _logger.LogInformation($"Fetching audit logs for tenant {tenantId}");

                // Get audit logs from storage
                var logs = await _storageService.GetAuditLogsAsync(tenantId, maxResults: 100);

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
                _logger.LogError(ex, "Error getting audit logs");

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
