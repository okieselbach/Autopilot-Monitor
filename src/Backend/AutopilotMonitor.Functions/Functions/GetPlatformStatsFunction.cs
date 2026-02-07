using System.Net;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions
{
    /// <summary>
    /// Public endpoint - no authentication required.
    /// Returns pre-computed platform stats for the landing page.
    /// </summary>
    public class GetPlatformStatsFunction
    {
        private readonly ILogger<GetPlatformStatsFunction> _logger;
        private readonly TableStorageService _storageService;

        public GetPlatformStatsFunction(ILogger<GetPlatformStatsFunction> logger, TableStorageService storageService)
        {
            _logger = logger;
            _storageService = storageService;
        }

        [Function("GetPlatformStats")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "platform-stats")] HttpRequestData req)
        {
            try
            {
                var stats = await _storageService.GetPlatformStatsAsync();

                var response = req.CreateResponse(HttpStatusCode.OK);

                if (stats == null)
                {
                    // No stats computed yet - return zeros
                    await response.WriteAsJsonAsync(new
                    {
                        totalEnrollments = 0,
                        totalUsers = 0,
                        totalTenants = 0,
                        uniqueDeviceModels = 0,
                        totalEventsProcessed = 0,
                        successfulEnrollments = 0,
                        issuesDetected = 0
                    });
                }
                else
                {
                    await response.WriteAsJsonAsync(new
                    {
                        totalEnrollments = stats.TotalEnrollments,
                        totalUsers = stats.TotalUsers,
                        totalTenants = stats.TotalTenants,
                        uniqueDeviceModels = stats.UniqueDeviceModels,
                        totalEventsProcessed = stats.TotalEventsProcessed,
                        successfulEnrollments = stats.SuccessfulEnrollments,
                        issuesDetected = stats.IssuesDetected
                    });
                }

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching platform stats");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { error = "Failed to retrieve platform stats" });
                return errorResponse;
            }
        }
    }
}
