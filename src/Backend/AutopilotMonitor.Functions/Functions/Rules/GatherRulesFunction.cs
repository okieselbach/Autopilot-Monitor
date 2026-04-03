using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Functions.Rules
{
    /// <summary>
    /// CRUD API for managing gather rules (portal-facing, JWT auth)
    /// </summary>
    public class GatherRulesFunction
    {
        private readonly ILogger<GatherRulesFunction> _logger;
        private readonly GatherRuleService _ruleService;

        public GatherRulesFunction(
            ILogger<GatherRulesFunction> logger,
            GatherRuleService ruleService)
        {
            _logger = logger;
            _ruleService = ruleService;
        }

        [Function("GetGatherRules")]
        public async Task<HttpResponseData> GetRules(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "rules/gather")] HttpRequestData req)
        {
            // Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware
            var tenantId = TenantHelper.GetTenantId(req);

            var rules = await _ruleService.GetAllRulesForTenantAsync(tenantId);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { success = true, rules });
            return response;
        }

        [Function("CreateGatherRule")]
        public async Task<HttpResponseData> CreateRule(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "rules/gather")] HttpRequestData req)
        {
            // Authentication + TenantAdminOrGA authorization enforced by PolicyEnforcementMiddleware
            var tenantId = TenantHelper.GetTenantId(req);

            if (req.Headers.TryGetValues("Content-Length", out var clValues)
                && long.TryParse(clValues.FirstOrDefault(), out var contentLength)
                && contentLength > 1_048_576) // 1 MB limit
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { success = false, message = "Request body too large" });
                return badRequest;
            }
            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var rule = JsonConvert.DeserializeObject<GatherRule>(body);

            if (rule == null || string.IsNullOrEmpty(rule.RuleId))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { success = false, message = "Invalid rule data" });
                return badRequest;
            }

            try
            {
                var success = await _ruleService.CreateRuleAsync(tenantId, rule);

                var response = req.CreateResponse(success ? HttpStatusCode.Created : HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { success, message = success ? "Rule created" : "Failed to create rule" });
                return response;
            }
            catch (InvalidOperationException ex)
            {
                var conflict = req.CreateResponse(HttpStatusCode.Conflict);
                await conflict.WriteAsJsonAsync(new { success = false, message = ex.Message });
                return conflict;
            }
        }

        [Function("UpdateGatherRule")]
        public async Task<HttpResponseData> UpdateRule(
            [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "rules/gather/{ruleId}")] HttpRequestData req,
            string ruleId)
        {
            // Authentication + TenantAdminOrGA authorization enforced by PolicyEnforcementMiddleware
            var tenantId = TenantHelper.GetTenantId(req);

            if (req.Headers.TryGetValues("Content-Length", out var clValues2)
                && long.TryParse(clValues2.FirstOrDefault(), out var contentLength2)
                && contentLength2 > 1_048_576) // 1 MB limit
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { success = false, message = "Request body too large" });
                return badRequest;
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
            [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "rules/gather/{ruleId}")] HttpRequestData req,
            string ruleId)
        {
            // Authentication + TenantAdminOrGA authorization enforced by PolicyEnforcementMiddleware
            var tenantId = TenantHelper.GetTenantId(req);

            // Load the rule to determine its type (built-in/community vs. custom)
            var rules = await _ruleService.GetAllRulesForTenantAsync(tenantId);
            var rule = rules.FirstOrDefault(r => r.RuleId == ruleId);

            if (rule == null)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteAsJsonAsync(new { success = false, message = "Rule not found" });
                return notFound;
            }

            var success = await _ruleService.DeleteRuleAsync(tenantId, rule);
            var response = req.CreateResponse(success ? HttpStatusCode.OK : HttpStatusCode.InternalServerError);
            await response.WriteAsJsonAsync(new { success, message = success ? "Rule deleted" : "Failed to delete rule" });
            return response;
        }
    }
}
