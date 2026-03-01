using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions
{
    public class GetSessionReportsFunction
    {
        private readonly ILogger<GetSessionReportsFunction> _logger;
        private readonly SessionReportService _sessionReportService;
        private readonly GalacticAdminService _galacticAdminService;

        public GetSessionReportsFunction(
            ILogger<GetSessionReportsFunction> logger,
            SessionReportService sessionReportService,
            GalacticAdminService galacticAdminService)
        {
            _logger = logger;
            _sessionReportService = sessionReportService;
            _galacticAdminService = galacticAdminService;
        }

        [Function("GetSessionReports")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "session-reports")] HttpRequestData req)
        {
            _logger.LogInformation("GetSessionReports function processing request");

            try
            {
                // Validate authentication
                if (!TenantHelper.IsAuthenticated(req))
                {
                    var unauthorizedResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                    await unauthorizedResponse.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Authentication required."
                    });
                    return unauthorizedResponse;
                }

                string userIdentifier = TenantHelper.GetUserIdentifier(req);

                // Require Galactic Admin only (this is admin-config data)
                var isGalacticAdmin = await _galacticAdminService.IsGalacticAdminAsync(userIdentifier);
                if (!isGalacticAdmin)
                {
                    var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
                    await forbiddenResponse.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Access denied. Galactic Admin role required."
                    });
                    return forbiddenResponse;
                }

                var reports = await _sessionReportService.GetAllReportsAsync();

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    reports
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching session reports");

                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "Internal server error"
                });
                return errorResponse;
            }
        }
    }
}
