using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Functions
{
    /// <summary>
    /// CRUD API for managing gather rules (portal-facing, JWT auth)
    /// </summary>
    public class GatherRulesFunction
    {
        private readonly ILogger<GatherRulesFunction> _logger;
        private readonly GatherRuleService _ruleService;

        public GatherRulesFunction(ILogger<GatherRulesFunction> logger, GatherRuleService ruleService)
        {
            _logger = logger;
            _ruleService = ruleService;
        }

        [Function("GetGatherRules")]
        public async Task<HttpResponseData> GetRules(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "gather-rules")] HttpRequestData req)
        {
            var tenantId = TenantHelper.IsAuthenticated(req) ? TenantHelper.GetTenantId(req) : null;
            if (string.IsNullOrEmpty(tenantId))
            {
                var unauthorized = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauthorized.WriteAsJsonAsync(new { success = false, message = "Authentication required" });
                return unauthorized;
            }

            var rules = await _ruleService.GetAllRulesForTenantAsync(tenantId);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { success = true, rules });
            return response;
        }

        [Function("CreateGatherRule")]
        public async Task<HttpResponseData> CreateRule(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "gather-rules")] HttpRequestData req)
        {
            var tenantId = TenantHelper.IsAuthenticated(req) ? TenantHelper.GetTenantId(req) : null;
            if (string.IsNullOrEmpty(tenantId))
            {
                var unauthorized = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauthorized.WriteAsJsonAsync(new { success = false, message = "Authentication required" });
                return unauthorized;
            }

            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var rule = JsonConvert.DeserializeObject<GatherRule>(body);

            if (rule == null || string.IsNullOrEmpty(rule.RuleId))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { success = false, message = "Invalid rule data" });
                return badRequest;
            }

            var success = await _ruleService.CreateRuleAsync(tenantId, rule);

            var response = req.CreateResponse(success ? HttpStatusCode.Created : HttpStatusCode.InternalServerError);
            await response.WriteAsJsonAsync(new { success, message = success ? "Rule created" : "Failed to create rule" });
            return response;
        }

        [Function("UpdateGatherRule")]
        public async Task<HttpResponseData> UpdateRule(
            [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "gather-rules/{ruleId}")] HttpRequestData req,
            string ruleId)
        {
            var tenantId = TenantHelper.IsAuthenticated(req) ? TenantHelper.GetTenantId(req) : null;
            if (string.IsNullOrEmpty(tenantId))
            {
                var unauthorized = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauthorized.WriteAsJsonAsync(new { success = false, message = "Authentication required" });
                return unauthorized;
            }

            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var rule = JsonConvert.DeserializeObject<GatherRule>(body);

            if (rule == null)
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { success = false, message = "Invalid rule data" });
                return badRequest;
            }

            rule.RuleId = ruleId;
            var success = await _ruleService.UpdateRuleAsync(tenantId, rule);

            var response = req.CreateResponse(success ? HttpStatusCode.OK : HttpStatusCode.InternalServerError);
            await response.WriteAsJsonAsync(new { success, message = success ? "Rule updated" : "Failed to update rule" });
            return response;
        }

        [Function("DeleteGatherRule")]
        public async Task<HttpResponseData> DeleteRule(
            [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "gather-rules/{ruleId}")] HttpRequestData req,
            string ruleId)
        {
            var tenantId = TenantHelper.IsAuthenticated(req) ? TenantHelper.GetTenantId(req) : null;
            if (string.IsNullOrEmpty(tenantId))
            {
                var unauthorized = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauthorized.WriteAsJsonAsync(new { success = false, message = "Authentication required" });
                return unauthorized;
            }

            var success = await _ruleService.DeleteRuleAsync(tenantId, ruleId);

            var response = req.CreateResponse(success ? HttpStatusCode.OK : HttpStatusCode.InternalServerError);
            await response.WriteAsJsonAsync(new { success, message = success ? "Rule deleted" : "Failed to delete rule" });
            return response;
        }
    }
}
