using System.Net;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Reports
{
    public class GetSessionReportsFunction
    {
        private readonly ILogger<GetSessionReportsFunction> _logger;
        private readonly SessionReportService _sessionReportService;

        public GetSessionReportsFunction(
            ILogger<GetSessionReportsFunction> logger,
            SessionReportService sessionReportService)
        {
            _logger = logger;
            _sessionReportService = sessionReportService;
        }

        [Function("GetSessionReports")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "galactic/session-reports")] HttpRequestData req)
        {
            _logger.LogInformation("GetSessionReports function processing request");

            try
            {
                // Authentication + GalacticAdminOnly authorization enforced by PolicyEnforcementMiddleware

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
