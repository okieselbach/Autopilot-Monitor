using System.Net;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Rules
{
    /// <summary>
    /// Galactic Admin endpoints for viewing rules across tenants.
    /// Reads tenantId from query string (not JWT) so galactic admins can inspect any tenant.
    /// </summary>
    public class GalacticRulesFunction
    {
        private readonly ILogger<GalacticRulesFunction> _logger;
        private readonly GatherRuleService _gatherRuleService;
        private readonly AnalyzeRuleService _analyzeRuleService;

        public GalacticRulesFunction(
            ILogger<GalacticRulesFunction> logger,
            GatherRuleService gatherRuleService,
            AnalyzeRuleService analyzeRuleService)
        {
            _logger = logger;
            _gatherRuleService = gatherRuleService;
            _analyzeRuleService = analyzeRuleService;
        }

        /// <summary>
        /// GET /api/galactic/rules/gather?tenantId=X - Get gather rules for any tenant (Galactic Admin only)
        /// </summary>
        [Function("GetGalacticGatherRules")]
        public async Task<HttpResponseData> GetGatherRules(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "galactic/rules/gather")] HttpRequestData req)
        {
            // Authentication + GalacticAdminOnly authorization enforced by PolicyEnforcementMiddleware
            var tenantId = System.Web.HttpUtility.ParseQueryString(req.Url.Query ?? "").Get("tenantId");

            if (string.IsNullOrEmpty(tenantId))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { success = false, message = "tenantId query parameter is required" });
                return badRequest;
            }

            _logger.LogInformation("Galactic admin requesting gather rules for tenant {TenantId}", tenantId);

            var rules = await _gatherRuleService.GetAllRulesForTenantAsync(tenantId);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { success = true, rules });
            return response;
        }

        /// <summary>
        /// GET /api/galactic/rules/analyze?tenantId=X - Get analyze rules for any tenant (Galactic Admin only)
        /// </summary>
        [Function("GetGalacticAnalyzeRules")]
        public async Task<HttpResponseData> GetAnalyzeRules(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "galactic/rules/analyze")] HttpRequestData req)
        {
            // Authentication + GalacticAdminOnly authorization enforced by PolicyEnforcementMiddleware
            var tenantId = System.Web.HttpUtility.ParseQueryString(req.Url.Query ?? "").Get("tenantId");

            if (string.IsNullOrEmpty(tenantId))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { success = false, message = "tenantId query parameter is required" });
                return badRequest;
            }

            _logger.LogInformation("Galactic admin requesting analyze rules for tenant {TenantId}", tenantId);

            var rules = await _analyzeRuleService.GetAllRulesForTenantAsync(tenantId);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { success = true, rules });
            return response;
        }
    }
}
