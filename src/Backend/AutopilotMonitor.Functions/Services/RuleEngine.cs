using System.Text.RegularExpressions;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Evaluates analyze rules against session events to detect issues.
    /// Runs once at enrollment end or on-demand via "Analyze Now" button.
    /// All rules (single + correlation) are evaluated in a single pass over all events.
    /// </summary>
    public partial class RuleEngine
    {
        private readonly AnalyzeRuleService _ruleService;
        private readonly IRuleRepository _ruleRepo;
        private readonly ISessionRepository _sessionRepo;
        private readonly ILogger _logger;

        public RuleEngine(AnalyzeRuleService ruleService, IRuleRepository ruleRepo, ISessionRepository sessionRepo, ILogger logger)
        {
            _ruleService = ruleService;
            _ruleRepo = ruleRepo;
            _sessionRepo = sessionRepo;
            _logger = logger;
        }

        /// <summary>
        /// Evaluates ALL active analyze rules against the full session event stream.
        /// Called once at enrollment end or on-demand. Fetches events internally.
        /// When reanalyze=true, all rules are re-evaluated regardless of existing results.
        /// </summary>
        public async Task<List<RuleResult>> AnalyzeSessionAsync(string tenantId, string sessionId, bool reanalyze = false)
        {
            var results = new List<RuleResult>();

            try
            {
                var activeRules = await _ruleService.GetActiveRulesForTenantAsync(tenantId);
                var allEvents = await _sessionRepo.GetSessionEventsAsync(tenantId, sessionId);

                if (allEvents.Count == 0)
                {
                    _logger.LogInformation($"No events found for session {sessionId}, skipping analysis");
                    return results;
                }

                // On reanalyze: skip deduplication so all rules are re-evaluated from scratch
                // On normal run: skip rules already evaluated to avoid duplicate storage
                HashSet<string> existingRuleIds;
                if (reanalyze)
                {
                    existingRuleIds = new HashSet<string>();
                }
                else
                {
                    var existingResults = await _ruleRepo.GetRuleResultsAsync(tenantId, sessionId);
                    existingRuleIds = new HashSet<string>(existingResults.Select(r => r.RuleId));
                }

                _logger.LogInformation($"Analyzing session {sessionId}: {allEvents.Count} events, {activeRules.Count} rules ({existingRuleIds.Count} already evaluated)");

                foreach (var rule in activeRules)
                {
                    try
                    {
                        // Skip if we already have a result for this rule
                        if (existingRuleIds.Contains(rule.RuleId))
                            continue;

                        var result = EvaluateRule(rule, allEvents);
                        if (result != null)
                        {
                            result.SessionId = sessionId;
                            result.TenantId = tenantId;
                            results.Add(result);
                            _logger.LogInformation($"Rule {rule.RuleId} ({rule.Trigger}) fired for session {sessionId} with confidence {result.ConfidenceScore}%");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Error evaluating rule {rule.RuleId}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error analyzing session");
            }

            return results;
        }

        /// <summary>
        /// Evaluates a single rule against the full session event stream
        /// </summary>
        private RuleResult? EvaluateRule(AnalyzeRule rule, List<EnrollmentEvent> events)
        {
            var matchedConditions = new Dictionary<string, object>();
            int confidence = rule.BaseConfidence;
            bool allRequiredMet = true;

            // Evaluate each condition
            foreach (var condition in rule.Conditions)
            {
                var (matched, evidence) = EvaluateCondition(condition, events);

                if (condition.Required && !matched)
                {
                    allRequiredMet = false;
                    break;
                }

                if (matched)
                {
                    matchedConditions[condition.Signal] = evidence;
                }
            }

            if (!allRequiredMet)
                return null;

            // Safety net: if no conditions matched at all, the rule should not fire.
            // This prevents rules with all-optional conditions from vacuously triggering.
            if (matchedConditions.Count == 0)
                return null;

            // Calculate confidence from factors
            foreach (var factor in rule.ConfidenceFactors)
            {
                if (EvaluateConfidenceFactor(factor, events, matchedConditions))
                {
                    confidence += factor.Weight;
                    matchedConditions[$"factor_{factor.Signal}"] = true;
                }
            }

            // Cap confidence at 100
            confidence = Math.Min(confidence, 100);

            // Check threshold
            if (confidence < rule.ConfidenceThreshold)
                return null;

            return new RuleResult
            {
                RuleId = rule.RuleId,
                RuleTitle = rule.Title,
                Severity = rule.Severity,
                Category = rule.Category,
                ConfidenceScore = confidence,
                Explanation = rule.Explanation,
                Remediation = rule.Remediation,
                RelatedDocs = rule.RelatedDocs,
                MatchedConditions = matchedConditions,
                DetectedAt = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Evaluates a single condition against the event stream
        /// </summary>
    }
}
