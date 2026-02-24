using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions
{
    /// <summary>
    /// Returns analysis results (rule evaluations) for a session.
    /// Supports on-demand re-analysis via ?reanalyze=true query parameter.
    /// </summary>
    public class GetRuleResultsFunction
    {
        private readonly ILogger<GetRuleResultsFunction> _logger;
        private readonly TableStorageService _storageService;
        private readonly AnalyzeRuleService _analyzeRuleService;

        public GetRuleResultsFunction(
            ILogger<GetRuleResultsFunction> logger,
            TableStorageService storageService,
            AnalyzeRuleService analyzeRuleService)
        {
            _logger = logger;
            _storageService = storageService;
            _analyzeRuleService = analyzeRuleService;
        }

        [Function("GetRuleResults")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sessions/{sessionId}/analysis")] HttpRequestData req,
            string sessionId)
        {
            var tenantId = TenantHelper.IsAuthenticated(req) ? TenantHelper.GetTenantId(req) : null;
            if (string.IsNullOrEmpty(tenantId))
            {
                var unauthorized = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauthorized.WriteAsJsonAsync(new { success = false, message = "Authentication required" });
                return unauthorized;
            }

            // On-demand re-analysis: run all rules against all session events
            var query2 = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var reanalyze = string.Equals(query2["reanalyze"], "true", StringComparison.OrdinalIgnoreCase);

            if (reanalyze)
            {
                try
                {
                    var ruleEngine = new RuleEngine(_analyzeRuleService, _storageService, _logger);
                    var freshResults = await ruleEngine.AnalyzeSessionAsync(tenantId, sessionId, reanalyze: true);

                    // Delete existing results so stale entries don't persist after re-analysis
                    await _storageService.DeleteSessionRuleResultsAsync(tenantId, sessionId);

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
