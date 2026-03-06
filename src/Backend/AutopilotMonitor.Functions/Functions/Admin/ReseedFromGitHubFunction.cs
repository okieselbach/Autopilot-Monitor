using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions
{
    /// <summary>
    /// Central reseed endpoint that fetches all rule types from GitHub and writes them to Table Storage.
    /// Replaces the old per-type reseed endpoints (gather-rules/reseed, analyze-rules/reseed).
    /// </summary>
    public class ReseedFromGitHubFunction
    {
        private readonly ILogger<ReseedFromGitHubFunction> _logger;
        private readonly GitHubRuleRepository _gitHubRepo;
        private readonly GatherRuleService _gatherRuleService;
        private readonly AnalyzeRuleService _analyzeRuleService;
        private readonly ImeLogPatternService _imeLogPatternService;
        private readonly TableStorageService _storageService;
        private readonly GalacticAdminService _galacticAdminService;

        public ReseedFromGitHubFunction(
            ILogger<ReseedFromGitHubFunction> logger,
            GitHubRuleRepository gitHubRepo,
            GatherRuleService gatherRuleService,
            AnalyzeRuleService analyzeRuleService,
            ImeLogPatternService imeLogPatternService,
            TableStorageService storageService,
            GalacticAdminService galacticAdminService)
        {
            _logger = logger;
            _gitHubRepo = gitHubRepo;
            _gatherRuleService = gatherRuleService;
            _analyzeRuleService = analyzeRuleService;
            _imeLogPatternService = imeLogPatternService;
            _storageService = storageService;
            _galacticAdminService = galacticAdminService;
        }

        [Function("ReseedFromGitHub")]
        public async Task<HttpResponseData> Reseed(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "rules/reseed-from-github")] HttpRequestData req)
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

                // Parse ?type= parameter (default: all)
                var typeParam = "all";
                var queryString = req.Url.Query;
                if (queryString.Contains("type=", StringComparison.OrdinalIgnoreCase))
                {
                    var typeStart = queryString.IndexOf("type=", StringComparison.OrdinalIgnoreCase) + 5;
                    var typeEnd = queryString.IndexOf('&', typeStart);
                    typeParam = (typeEnd > 0 ? queryString.Substring(typeStart, typeEnd - typeStart) : queryString.Substring(typeStart)).ToLowerInvariant();
                }

                _logger.LogInformation($"Reseed from GitHub triggered by Galactic Admin {upn}, type={typeParam}");

                var gatherResult = new { deleted = 0, written = 0 };
                var analyzeResult = new { deleted = 0, written = 0 };
                var imeResult = new { deleted = 0, written = 0 };

                if (typeParam == "all" || typeParam == "gather")
                {
                    var rules = await _gitHubRepo.FetchGatherRulesAsync();
                    var (d, w) = await ReseedGatherAsync(rules);
                    gatherResult = new { deleted = d, written = w };
                }

                if (typeParam == "all" || typeParam == "analyze")
                {
                    var rules = await _gitHubRepo.FetchAnalyzeRulesAsync();
                    var (d, w) = await ReseedAnalyzeAsync(rules);
                    analyzeResult = new { deleted = d, written = w };
                }

                if (typeParam == "all" || typeParam == "ime")
                {
                    var patterns = await _gitHubRepo.FetchImeLogPatternsAsync();
                    var (d, w) = await ReseedImeAsync(patterns);
                    imeResult = new { deleted = d, written = w };
                }

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    message = "Reseed from GitHub complete",
                    gather = gatherResult,
                    analyze = analyzeResult,
                    ime = imeResult
                });
                return response;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Failed to fetch rules from GitHub");
                var response = req.CreateResponse(HttpStatusCode.BadGateway);
                await response.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "Failed to fetch rules from GitHub. GitHub CDN may cache responses for up to 5 minutes after a merge."
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during GitHub reseed");
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { success = false, message = "Failed to reseed from GitHub" });
                return response;
            }
        }

        private async Task<(int deleted, int written)> ReseedGatherAsync(List<AutopilotMonitor.Shared.Models.GatherRule> rules)
        {
            var existing = await _storageService.GetGatherRulesAsync("global");
            var deleted = 0;
            foreach (var rule in existing.Where(r => r.IsBuiltIn || r.IsCommunity))
            {
                await _storageService.DeleteGatherRuleAsync("global", rule.RuleId);
                deleted++;
            }

            foreach (var rule in rules)
            {
                // Community rules keep IsCommunity=true from JSON; all others are built-in
                if (!rule.IsCommunity)
                    rule.IsBuiltIn = true;
                rule.CreatedAt = DateTime.UtcNow;
                rule.UpdatedAt = DateTime.UtcNow;
                await _storageService.StoreGatherRuleAsync(rule, "global");
            }

            _logger.LogInformation($"GitHub reseed gather: {deleted} deleted, {rules.Count} written");
            return (deleted, rules.Count);
        }

        private async Task<(int deleted, int written)> ReseedAnalyzeAsync(List<AutopilotMonitor.Shared.Models.AnalyzeRule> rules)
        {
            var existing = await _storageService.GetAnalyzeRulesAsync("global");
            var deleted = 0;
            foreach (var rule in existing.Where(r => r.IsBuiltIn || r.IsCommunity))
            {
                await _storageService.DeleteAnalyzeRuleAsync("global", rule.RuleId);
                deleted++;
            }

            foreach (var rule in rules)
            {
                // Community rules keep IsCommunity=true from JSON; all others are built-in
                if (!rule.IsCommunity)
                    rule.IsBuiltIn = true;
                rule.CreatedAt = DateTime.UtcNow;
                rule.UpdatedAt = DateTime.UtcNow;
                await _storageService.StoreAnalyzeRuleAsync(rule, "global");
            }

            _logger.LogInformation($"GitHub reseed analyze: {deleted} deleted, {rules.Count} written");
            return (deleted, rules.Count);
        }

        private async Task<(int deleted, int written)> ReseedImeAsync(List<AutopilotMonitor.Shared.Models.ImeLogPattern> patterns)
        {
            var existing = await _storageService.GetImeLogPatternsAsync("global");
            var deleted = 0;
            foreach (var pattern in existing.Where(p => p.IsBuiltIn))
            {
                await _storageService.DeleteImeLogPatternAsync("global", pattern.PatternId);
                deleted++;
            }

            foreach (var pattern in patterns)
            {
                pattern.IsBuiltIn = true;
                await _storageService.StoreImeLogPatternAsync(pattern, "global");
            }

            _logger.LogInformation($"GitHub reseed IME: {deleted} deleted, {patterns.Count} written");
            return (deleted, patterns.Count);
        }
    }
}
