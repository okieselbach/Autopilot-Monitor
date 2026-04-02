using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Admin
{
    public class GetGlobalAuditLogsFunction
    {
        private readonly ILogger<GetGlobalAuditLogsFunction> _logger;
        private readonly IMaintenanceRepository _maintenanceRepo;

        public GetGlobalAuditLogsFunction(
            ILogger<GetGlobalAuditLogsFunction> logger,
            IMaintenanceRepository maintenanceRepo)
        {
            _logger = logger;
            _maintenanceRepo = maintenanceRepo;
        }

        [Function("GetGlobalAuditLogs")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/audit/logs")] HttpRequestData req)
        {
            _logger.LogInformation("GetGlobalAuditLogs function processing request (Global Admin Mode)");

            try
            {
                // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
                var userEmail = TenantHelper.GetUserIdentifier(req);

                _logger.LogInformation($"Fetching all audit logs across all tenants (User: {userEmail})");

                var logs = await _maintenanceRepo.GetAllAuditLogsAsync(maxResults: 100);

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
                return await req.InternalServerErrorAsync(_logger, ex, "Get global audit logs");
            }
        }
    }
}
