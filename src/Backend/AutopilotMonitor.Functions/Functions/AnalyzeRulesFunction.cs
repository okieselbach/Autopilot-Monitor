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
    /// CRUD API for managing analyze rules (portal-facing, JWT auth)
    /// </summary>
    public class AnalyzeRulesFunction
    {
        private readonly ILogger<AnalyzeRulesFunction> _logger;
        private readonly AnalyzeRuleService _ruleService;
        private readonly GalacticAdminService _galacticAdminService;

        public AnalyzeRulesFunction(ILogger<AnalyzeRulesFunction> logger, AnalyzeRuleService ruleService, GalacticAdminService galacticAdminService)
        {
            _logger = logger;
            _ruleService = ruleService;
            _galacticAdminService = galacticAdminService;
        }

        [Function("GetAnalyzeRules")]
        public async Task<HttpResponseData> GetRules(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "analyze-rules")] HttpRequestData req)
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

        [Function("CreateAnalyzeRule")]
        public async Task<HttpResponseData> CreateRule(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "analyze-rules")] HttpRequestData req)
        {
            var tenantId = TenantHelper.IsAuthenticated(req) ? TenantHelper.GetTenantId(req) : null;
            if (string.IsNullOrEmpty(tenantId))
            {
                var unauthorized = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauthorized.WriteAsJsonAsync(new { success = false, message = "Authentication required" });
                return unauthorized;
            }

            if (req.Headers.TryGetValues("Content-Length", out var clValues)
                && long.TryParse(clValues.FirstOrDefault(), out var contentLength)
                && contentLength > 1_048_576) // 1 MB limit
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { success = false, message = "Request body too large" });
                return badRequest;
            }
            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var rule = JsonConvert.DeserializeObject<AnalyzeRule>(body);

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

        [Function("UpdateAnalyzeRule")]
        public async Task<HttpResponseData> UpdateRule(
            [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "analyze-rules/{ruleId}")] HttpRequestData req,
            string ruleId)
        {
            var tenantId = TenantHelper.IsAuthenticated(req) ? TenantHelper.GetTenantId(req) : null;
            if (string.IsNullOrEmpty(tenantId))
            {
                var unauthorized = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauthorized.WriteAsJsonAsync(new { success = false, message = "Authentication required" });
                return unauthorized;
            }

            if (req.Headers.TryGetValues("Content-Length", out var clValues2)
                && long.TryParse(clValues2.FirstOrDefault(), out var contentLength2)
                && contentLength2 > 1_048_576) // 1 MB limit
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { success = false, message = "Request body too large" });
                return badRequest;
            }
            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var rule = JsonConvert.DeserializeObject<AnalyzeRule>(body);

            if (rule == null)
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { success = false, message = "Invalid rule data" });
                return badRequest;
            }

            rule.RuleId = ruleId;

            // Galactic Admins can edit built-in rules globally (affects all tenants)
            var globalEdit = req.Url.Query.Contains("global=true", StringComparison.OrdinalIgnoreCase);
            if (globalEdit)
            {
                var upn = TenantHelper.GetUserIdentifier(req);
                var isGalactic = await _galacticAdminService.IsGalacticAdminAsync(upn);
                if (!isGalactic)
                {
                    var forbidden = req.CreateResponse(HttpStatusCode.Forbidden);
                    await forbidden.WriteAsJsonAsync(new { success = false, message = "Galactic Admin privileges required for global rule edits" });
                    return forbidden;
                }

                var success = await _ruleService.UpdateGlobalRuleAsync(rule);
                var response = req.CreateResponse(success ? HttpStatusCode.OK : HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { success, message = success ? "Global rule updated" : "Failed to update global rule" });
                return response;
            }
            else
            {
                var success = await _ruleService.UpdateRuleAsync(tenantId, rule);
                var response = req.CreateResponse(success ? HttpStatusCode.OK : HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { success, message = success ? "Rule updated" : "Failed to update rule" });
                return response;
            }
        }

        [Function("ReseedAnalyzeRules")]
        public async Task<HttpResponseData> ReseedRules(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "analyze-rules/reseed")] HttpRequestData req)
        {
            try
            {
                if (!TenantHelper.IsAuthenticated(req))
                {
                    var unauthorized = req.CreateResponse(HttpStatusCode.Unauthorized);
                    await unauthorized.WriteAsJsonAsync(new { success = false, message = "Authentication required" });
                    return unauthorized;
                }

                var upn = TenantHelper.GetUserIdentifier(req);
                if (!await _galacticAdminService.IsGalacticAdminAsync(upn))
                {
                    var forbidden = req.CreateResponse(HttpStatusCode.Forbidden);
                    await forbidden.WriteAsJsonAsync(new { success = false, message = "Galactic Admin privileges required" });
                    return forbidden;
                }

                _logger.LogInformation($"Reseed analyze rules triggered by Galactic Admin {upn}");

                var (deleted, written) = await _ruleService.ReseedBuiltInRulesAsync();

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    message = $"Reseed complete: {deleted} old rules removed, {written} rules written from code.",
                    deleted,
                    written
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reseeding analyze rules");
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { success = false, message = "Failed to reseed analyze rules" });
                return response;
            }
        }

        [Function("DeleteAnalyzeRule")]
        public async Task<HttpResponseData> DeleteRule(
            [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "analyze-rules/{ruleId}")] HttpRequestData req,
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
