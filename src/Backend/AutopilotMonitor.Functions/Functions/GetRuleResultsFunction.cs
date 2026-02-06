using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions
{
    /// <summary>
    /// Returns analysis results (rule evaluations) for a session
    /// Used by the session detail page to show detected issues
    /// </summary>
    public class GetRuleResultsFunction
    {
        private readonly ILogger<GetRuleResultsFunction> _logger;
        private readonly TableStorageService _storageService;

        public GetRuleResultsFunction(ILogger<GetRuleResultsFunction> logger, TableStorageService storageService)
        {
            _logger = logger;
            _storageService = storageService;
        }

        [Function("GetRuleResults")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sessions/{sessionId}/analysis")] HttpRequestData req,
            string sessionId)
        {
            var tenantId = TenantHelper.IsAuthenticated(req) ? TenantHelper.GetTenantId(req) : null;
            if (string.IsNullOrEmpty(tenantId))
            {
                // Fall back to query parameter for cross-tenant access
                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                tenantId = query["tenantId"];
            }

            if (string.IsNullOrEmpty(tenantId))
            {
                var unauthorized = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauthorized.WriteAsJsonAsync(new { success = false, message = "Authentication required" });
                return unauthorized;
            }

            var results = await _storageService.GetRuleResultsAsync(tenantId, sessionId);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                sessionId,
                results,
                totalIssues = results.Count,
                criticalCount = results.Count(r => r.Severity == "critical"),
                highCount = results.Count(r => r.Severity == "high"),
                warningCount = results.Count(r => r.Severity == "warning")
            });
            return response;
        }
    }
}
