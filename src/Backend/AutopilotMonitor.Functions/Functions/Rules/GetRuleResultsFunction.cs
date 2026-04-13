using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
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
        private readonly IRuleRepository _ruleRepo;
        private readonly IMaintenanceRepository _maintenanceRepo;
        private readonly ISessionRepository _sessionRepo;
        private readonly AnalyzeRuleService _analyzeRuleService;

        public GetRuleResultsFunction(
            ILogger<GetRuleResultsFunction> logger,
            IRuleRepository ruleRepo,
            IMaintenanceRepository maintenanceRepo,
            ISessionRepository sessionRepo,
            AnalyzeRuleService analyzeRuleService)
        {
            _logger = logger;
            _ruleRepo = ruleRepo;
            _maintenanceRepo = maintenanceRepo;
            _sessionRepo = sessionRepo;
            _analyzeRuleService = analyzeRuleService;
        }

        [Function("GetRuleResults")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sessions/{sessionId}/analysis")] HttpRequestData req,
            string sessionId)
        {
            // Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware
            var requestCtx = req.GetRequestContext();
            var effectiveTenantId = requestCtx.TargetTenantId;

            // Global Admin cross-tenant fallback: resolve actual tenant upfront
            // (needed for both read and write paths)
            if (requestCtx.IsGlobalAdmin)
            {
                var resolvedTenantId = await _sessionRepo.FindSessionTenantIdAsync(sessionId);
                if (resolvedTenantId != null && !string.Equals(resolvedTenantId, effectiveTenantId, StringComparison.OrdinalIgnoreCase))
                {
                    effectiveTenantId = resolvedTenantId;
                }
            }

            var reanalyze = string.Equals(req.Query["reanalyze"], "true", StringComparison.OrdinalIgnoreCase);

            if (reanalyze)
            {
                try
                {
                    var ruleEngine = new RuleEngine(_analyzeRuleService, _ruleRepo, _sessionRepo, _logger);
                    var outcome = await ruleEngine.AnalyzeSessionAsync(effectiveTenantId, sessionId, reanalyze: true);

                    // Delete existing results so stale entries don't persist after re-analysis
                    await _maintenanceRepo.DeleteSessionRuleResultsAsync(effectiveTenantId, sessionId);

                    foreach (var result in outcome.Results)
                    {
                        await _ruleRepo.StoreRuleResultAsync(result);
                    }

                    _logger.LogInformation($"On-demand re-analysis for session {sessionId}: {outcome.Results.Count} issue(s) detected");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"On-demand analysis failed for session {sessionId}");
                }
            }

            var results = await _ruleRepo.GetRuleResultsAsync(effectiveTenantId, sessionId);

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
