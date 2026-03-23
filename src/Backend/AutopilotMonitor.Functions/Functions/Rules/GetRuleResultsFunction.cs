using System.Net;
using System.Web;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Rules
{
    /// <summary>
    /// Returns analysis results (rule evaluations) for a session.
    /// Supports on-demand re-analysis via ?reanalyze=true query parameter.
    /// Global admins can request analysis for any tenant by passing ?tenantId=...
    /// </summary>
    public class GetRuleResultsFunction
    {
        private readonly ILogger<GetRuleResultsFunction> _logger;
        private readonly TableStorageService _storageService;
        private readonly AnalyzeRuleService _analyzeRuleService;
        private readonly GlobalAdminService _globalAdminService;

        public GetRuleResultsFunction(
            ILogger<GetRuleResultsFunction> logger,
            TableStorageService storageService,
            AnalyzeRuleService analyzeRuleService,
            GlobalAdminService globalAdminService)
        {
            _logger = logger;
            _storageService = storageService;
            _analyzeRuleService = analyzeRuleService;
            _globalAdminService = globalAdminService;
        }

        [Function("GetRuleResults")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sessions/{sessionId}/analysis")] HttpRequestData req,
            string sessionId)
        {
            // Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware
            var userTenantId = TenantHelper.GetTenantId(req);
            var userIdentifier = TenantHelper.GetUserIdentifier(req);

            // Resolve effective tenant ID: use query param if provided, fall back to JWT tenant
            var query = HttpUtility.ParseQueryString(req.Url.Query);
            var requestedTenantId = query["tenantId"];
            var effectiveTenantId = string.IsNullOrEmpty(requestedTenantId) ? userTenantId : requestedTenantId;

            // Cross-tenant access requires Global Admin
            if (effectiveTenantId != userTenantId)
            {
                var isGlobalAdmin = await _globalAdminService.IsGlobalAdminAsync(userIdentifier);
                if (!isGlobalAdmin)
                {
                    var forbidden = req.CreateResponse(HttpStatusCode.Forbidden);
                    await forbidden.WriteAsJsonAsync(new { success = false, message = "Access denied. You can only view analysis results for your own tenant." });
                    return forbidden;
                }

                _logger.LogInformation($"Global Admin {userIdentifier} accessing cross-tenant analysis results (tenant: {effectiveTenantId})");
            }

            var reanalyze = string.Equals(query["reanalyze"], "true", StringComparison.OrdinalIgnoreCase);

            if (reanalyze)
            {
                try
                {
                    var ruleEngine = new RuleEngine(_analyzeRuleService, _storageService, _logger);
                    var freshResults = await ruleEngine.AnalyzeSessionAsync(effectiveTenantId, sessionId, reanalyze: true);

                    // Delete existing results so stale entries don't persist after re-analysis
                    await _storageService.DeleteSessionRuleResultsAsync(effectiveTenantId, sessionId);

                    foreach (var result in freshResults)
                    {
                        await _storageService.StoreRuleResultAsync(result);
                    }

                    _logger.LogInformation($"On-demand re-analysis for session {sessionId}: {freshResults.Count} issue(s) detected");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"On-demand analysis failed for session {sessionId}");
                }
            }

            var results = await _storageService.GetRuleResultsAsync(effectiveTenantId, sessionId);

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
