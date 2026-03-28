using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Metrics
{
    /// <summary>
    /// Functions for retrieving per-user MCP/API usage metrics.
    /// </summary>
    public class McpUsageMetricsFunction
    {
        private readonly ILogger<McpUsageMetricsFunction> _logger;
        private readonly IUserUsageRepository _userUsageRepo;

        public McpUsageMetricsFunction(
            ILogger<McpUsageMetricsFunction> logger,
            IUserUsageRepository userUsageRepo)
        {
            _logger = logger;
            _userUsageRepo = userUsageRepo;
        }

        /// <summary>
        /// GET /api/metrics/mcp-usage/{userId}?dateFrom=&amp;dateTo= — Usage for a specific user
        /// </summary>
        [Function("GetMcpUserUsage")]
        public async Task<HttpResponseData> GetUserUsage(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "metrics/mcp-usage/{userId}")] HttpRequestData req,
            string userId)
        {
            _logger.LogInformation("MCP user usage requested: userId={UserId}", userId);

            try
            {
                var dateFrom = req.Query["dateFrom"];
                var dateTo = req.Query["dateTo"];

                var records = await _userUsageRepo.GetUsageByUserAsync(userId, dateFrom, dateTo);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new { userId, records });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting MCP user usage");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { error = "Internal server error" });
                return errorResponse;
            }
        }

        /// <summary>
        /// GET /api/global/metrics/mcp-usage?tenantId=&amp;dateFrom=&amp;dateTo= — Global usage
        /// </summary>
        [Function("GetGlobalMcpUsage")]
        public async Task<HttpResponseData> GetGlobalUsage(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/metrics/mcp-usage")] HttpRequestData req)
        {
            _logger.LogInformation("Global MCP usage requested");

            try
            {
                var tenantId = req.Query["tenantId"];
                var dateFrom = req.Query["dateFrom"];
                var dateTo = req.Query["dateTo"];

                var records = !string.IsNullOrEmpty(tenantId)
                    ? await _userUsageRepo.GetUsageByTenantAsync(tenantId, dateFrom, dateTo)
                    : await _userUsageRepo.GetUsageByTenantAsync("", dateFrom, dateTo);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new { tenantId, records });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting global MCP usage");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { error = "Internal server error" });
                return errorResponse;
            }
        }

        /// <summary>
        /// GET /api/global/metrics/mcp-usage/daily?tenantId=&amp;dateFrom=&amp;dateTo= — Daily summaries
        /// </summary>
        [Function("GetGlobalMcpUsageDaily")]
        public async Task<HttpResponseData> GetGlobalUsageDaily(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/metrics/mcp-usage/daily")] HttpRequestData req)
        {
            _logger.LogInformation("Global MCP daily usage requested");

            try
            {
                var tenantId = req.Query["tenantId"];
                var dateFrom = req.Query["dateFrom"];
                var dateTo = req.Query["dateTo"];

                var summaries = await _userUsageRepo.GetDailySummaryAsync(tenantId, dateFrom, dateTo);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new { tenantId, summaries });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting global MCP daily usage");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { error = "Internal server error" });
                return errorResponse;
            }
        }
    }
}
