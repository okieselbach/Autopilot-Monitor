using System.Net;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Rules
{
    /// <summary>
    /// Global Admin endpoints for viewing rules across tenants.
    /// Reads tenantId from query string (not JWT) so global admins can inspect any tenant.
    /// </summary>
    public class GlobalRulesFunction
    {
        private readonly ILogger<GlobalRulesFunction> _logger;
        private readonly GatherRuleService _gatherRuleService;
        private readonly AnalyzeRuleService _analyzeRuleService;

        public GlobalRulesFunction(
            ILogger<GlobalRulesFunction> logger,
            GatherRuleService gatherRuleService,
            AnalyzeRuleService analyzeRuleService)
        {
            _logger = logger;
            _gatherRuleService = gatherRuleService;
            _analyzeRuleService = analyzeRuleService;
        }

        /// <summary>
        /// GET /api/global/rules/gather?tenantId=X - Get gather rules for any tenant (Global Admin only)
        /// </summary>
        [Function("GetGlobalGatherRules")]
        public async Task<HttpResponseData> GetGatherRules(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/rules/gather")] HttpRequestData req)
        {
            // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
            var tenantId = System.Web.HttpUtility.ParseQueryString(req.Url.Query ?? "").Get("tenantId");

            if (string.IsNullOrEmpty(tenantId))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { success = false, message = "tenantId query parameter is required" });
                return badRequest;
            }

            _logger.LogInformation("Global admin requesting gather rules for tenant {TenantId}", tenantId);

            var rules = await _gatherRuleService.GetAllRulesForTenantAsync(tenantId);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { success = true, rules });
            return response;
        }

        /// <summary>
        /// GET /api/global/rules/analyze?tenantId=X - Get analyze rules for any tenant (Global Admin only)
        /// </summary>
        [Function("GetGlobalAnalyzeRules")]
        public async Task<HttpResponseData> GetAnalyzeRules(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/rules/analyze")] HttpRequestData req)
        {
            // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
            var tenantId = System.Web.HttpUtility.ParseQueryString(req.Url.Query ?? "").Get("tenantId");

            if (string.IsNullOrEmpty(tenantId))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { success = false, message = "tenantId query parameter is required" });
                return badRequest;
            }

            _logger.LogInformation("Global admin requesting analyze rules for tenant {TenantId}", tenantId);

            var rules = await _analyzeRuleService.GetAllRulesForTenantAsync(tenantId);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { success = true, rules });
            return response;
        }
    }
}
