using System.Net;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Admin
{
    public class ApiUsageFunction
    {
        private readonly ILogger<ApiUsageFunction> _logger;
        private readonly IApiUsageRepository _usageRepo;

        public ApiUsageFunction(ILogger<ApiUsageFunction> logger, IApiUsageRepository usageRepo)
        {
            _logger = logger;
            _usageRepo = usageRepo;
        }

        /// <summary>
        /// GET /api/api-keys/{keyId}/usage?dateFrom=&dateTo=
        /// Returns usage records for a specific API key.
        /// </summary>
        [Function("GetApiKeyUsage")]
        public async Task<HttpResponseData> GetKeyUsage(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "api-keys/{keyId}/usage")] HttpRequestData req,
            string keyId)
        {
            _logger.LogInformation("GetApiKeyUsage: keyId={KeyId}", keyId);

            try
            {
                var dateFrom = req.Query["dateFrom"];
                var dateTo = req.Query["dateTo"];

                var records = await _usageRepo.GetUsageByKeyAsync(keyId, dateFrom, dateTo);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    keyId,
                    dateFrom,
                    dateTo,
                    count = records.Count,
                    totalRequests = records.Sum(r => r.RequestCount),
                    records
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting API key usage");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { error = "Internal server error" });
                return errorResponse;
            }
        }

        /// <summary>
        /// GET /api/global/api-usage?tenantId=&dateFrom=&dateTo=
        /// Returns usage records across keys, optionally filtered by tenant.
        /// </summary>
        [Function("GetGlobalApiUsage")]
        public async Task<HttpResponseData> GetGlobalUsage(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/api-usage")] HttpRequestData req)
        {
            _logger.LogInformation("GetGlobalApiUsage processing request");

            try
            {
                var tenantId = req.Query["tenantId"];
                var dateFrom = req.Query["dateFrom"];
                var dateTo = req.Query["dateTo"];

                List<ApiUsageRecord> records;
                if (!string.IsNullOrEmpty(tenantId))
                {
                    records = await _usageRepo.GetUsageByTenantAsync(tenantId, dateFrom, dateTo);
                }
                else
                {
                    // All usage — query without tenant filter via daily summary
                    var summary = await _usageRepo.GetDailySummaryAsync(null, dateFrom, dateTo);
                    var response2 = req.CreateResponse(HttpStatusCode.OK);
                    await response2.WriteAsJsonAsync(new
                    {
                        tenantId = (string?)null,
                        dateFrom,
                        dateTo,
                        totalRequests = summary.Sum(s => s.TotalRequests),
                        summary
                    });
                    return response2;
                }

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    tenantId,
                    dateFrom,
                    dateTo,
                    count = records.Count,
                    totalRequests = records.Sum(r => r.RequestCount),
                    records
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting global API usage");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { error = "Internal server error" });
                return errorResponse;
            }
        }

        /// <summary>
        /// GET /api/global/api-usage/daily?tenantId=&dateFrom=&dateTo=
        /// Returns aggregated daily usage summaries.
        /// </summary>
        [Function("GetGlobalApiUsageDaily")]
        public async Task<HttpResponseData> GetDailyUsage(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/api-usage/daily")] HttpRequestData req)
        {
            _logger.LogInformation("GetGlobalApiUsageDaily processing request");

            try
            {
                var tenantId = req.Query["tenantId"];
                var dateFrom = req.Query["dateFrom"];
                var dateTo = req.Query["dateTo"];

                var summary = await _usageRepo.GetDailySummaryAsync(tenantId, dateFrom, dateTo);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    tenantId,
                    dateFrom,
                    dateTo,
                    days = summary.Count,
                    totalRequests = summary.Sum(s => s.TotalRequests),
                    summary
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting daily API usage");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { error = "Internal server error" });
                return errorResponse;
            }
        }
    }
}
