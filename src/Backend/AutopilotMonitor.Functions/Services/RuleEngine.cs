using System.Text.RegularExpressions;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Evaluates analyze rules against session events to detect issues.
    /// Runs once at enrollment end or on-demand via "Analyze Now" button.
    /// All rules (single + correlation) are evaluated in a single pass over all events.
    /// </summary>
    public class RuleEngine
    {
        private readonly AnalyzeRuleService _ruleService;
        private readonly TableStorageService _storageService;
        private readonly ILogger _logger;

        public RuleEngine(AnalyzeRuleService ruleService, TableStorageService storageService, ILogger logger)
        {
            _ruleService = ruleService;
            _storageService = storageService;
            _logger = logger;
        }

        /// <summary>
        /// Evaluates ALL active analyze rules against the full session event stream.
        /// Called once at enrollment end or on-demand. Fetches events internally.
        /// </summary>
        public async Task<List<RuleResult>> AnalyzeSessionAsync(string tenantId, string sessionId)
        {
            var results = new List<RuleResult>();

            try
            {
                var activeRules = await _ruleService.GetActiveRulesForTenantAsync(tenantId);
                var allEvents = await _storageService.GetSessionEventsAsync(tenantId, sessionId);

                if (allEvents.Count == 0)
                {
                    _logger.LogInformation($"No events found for session {sessionId}, skipping analysis");
                    return results;
                }

                // Get existing rule results to avoid duplicates
                var existingResults = await _storageService.GetRuleResultsAsync(tenantId, sessionId);
                var existingRuleIds = new HashSet<string>(existingResults.Select(r => r.RuleId));

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
        private (bool matched, object evidence) EvaluateCondition(RuleCondition condition, List<EnrollmentEvent> events)
        {
            switch (condition.Source)
            {
                case "event_type":
                    return EvaluateEventTypeCondition(condition, events);

                case "event_data":
                    return EvaluateEventDataCondition(condition, events);

                case "event_count":
                    return EvaluateEventCountCondition(condition, events);

                case "phase_duration":
                    return EvaluatePhaseDurationCondition(condition, events);

                default:
                    return (false, "unknown source");
            }
        }

        private (bool matched, object evidence) EvaluateEventTypeCondition(RuleCondition condition, List<EnrollmentEvent> events)
        {
            var matchingEvents = events.Where(e => MatchesEventType(e, condition.EventType)).ToList();

            if (!matchingEvents.Any())
                return (false, "no matching events");

            // If DataField is specified, check data within matching events
            if (!string.IsNullOrEmpty(condition.DataField))
            {
                foreach (var evt in matchingEvents)
                {
                    var fieldValue = GetDataFieldValue(evt, condition.DataField);
                    if (fieldValue != null && MatchesOperator(fieldValue, condition.Operator, condition.Value))
                    {
                        return (true, new { eventType = evt.EventType, field = condition.DataField, value = fieldValue });
                    }
                }
                return (false, "data field not matched");
            }

            // Just check if event type exists
            return (true, new { eventType = condition.EventType, count = matchingEvents.Count });
        }

        private (bool matched, object evidence) EvaluateEventDataCondition(RuleCondition condition, List<EnrollmentEvent> events)
        {
            var matchingEvents = events.Where(e => MatchesEventType(e, condition.EventType)).ToList();

            foreach (var evt in matchingEvents)
            {
                var fieldValue = GetDataFieldValue(evt, condition.DataField);
                if (fieldValue != null && MatchesOperator(fieldValue, condition.Operator, condition.Value))
                {
                    return (true, new { field = condition.DataField, value = fieldValue });
                }
            }

            return (false, "no matching data");
        }

        private (bool matched, object evidence) EvaluateEventCountCondition(RuleCondition condition, List<EnrollmentEvent> events)
        {
            var count = events.Count(e => MatchesEventType(e, condition.EventType));

            if (condition.Operator == "count_gte" && int.TryParse(condition.Value, out var threshold))
            {
                if (count >= threshold)
                    return (true, new { count, threshold });
            }

            return (false, new { count });
        }

        private (bool matched, object evidence) EvaluatePhaseDurationCondition(RuleCondition condition, List<EnrollmentEvent> events)
        {
            // Find phase change events to calculate phase duration
            var phaseEvents = events
                .Where(e => e.EventType == "esp_phase_changed")
                .OrderBy(e => e.Timestamp)
                .ToList();

            if (!phaseEvents.Any())
                return (false, "no phase events");

            // Check if a specific phase took too long
            var targetPhase = condition.Value;
            for (int i = 0; i < phaseEvents.Count; i++)
            {
                var phaseData = phaseEvents[i].Data;
                var currentPhase = phaseData?.ContainsKey("espPhase") == true
                    ? phaseData["espPhase"]?.ToString()
                    : null;

                if (currentPhase == targetPhase)
                {
                    // Calculate how long this phase lasted
                    DateTime phaseEnd;
                    if (i + 1 < phaseEvents.Count)
                    {
                        phaseEnd = phaseEvents[i + 1].Timestamp;
                    }
                    else
                    {
                        phaseEnd = DateTime.UtcNow; // Phase is still active
                    }

                    var duration = (phaseEnd - phaseEvents[i].Timestamp).TotalSeconds;
                    return (true, new { phase = targetPhase, durationSeconds = duration });
                }
            }

            return (false, "phase not found");
        }

        private bool EvaluateConfidenceFactor(ConfidenceFactor factor, List<EnrollmentEvent> events, Dictionary<string, object> matchedConditions)
        {
            // Simple confidence factor evaluation
            if (factor.Condition.StartsWith("phase_duration >"))
            {
                if (int.TryParse(factor.Condition.Replace("phase_duration >", "").Trim(), out var threshold))
                {
                    foreach (var mc in matchedConditions.Values)
                    {
                        if (mc is IDictionary<string, object> dict && dict.ContainsKey("durationSeconds"))
                        {
                            var duration = Convert.ToDouble(dict["durationSeconds"]);
                            return duration > threshold;
                        }
                    }
                }
            }
            else if (factor.Condition == "exists")
            {
                return matchedConditions.ContainsKey(factor.Signal);
            }
            else if (factor.Condition.StartsWith("count >="))
            {
                if (int.TryParse(factor.Condition.Replace("count >=", "").Trim(), out var threshold))
                {
                    var count = events.Count(e => e.EventType == factor.Signal);
                    return count >= threshold;
                }
            }

            return false;
        }

        // ===== HELPERS =====

        private bool MatchesEventType(EnrollmentEvent evt, string eventType)
        {
            if (string.IsNullOrEmpty(eventType))
                return false;

            return string.Equals(evt.EventType, eventType, StringComparison.OrdinalIgnoreCase);
        }

        private string? GetDataFieldValue(EnrollmentEvent evt, string dataField)
        {
            if (evt.Data == null || string.IsNullOrEmpty(dataField))
                return null;

            // Support checking message field directly
            if (dataField.Equals("message", StringComparison.OrdinalIgnoreCase))
                return evt.Message;

            // Check in Data dictionary
            if (evt.Data.TryGetValue(dataField, out var value))
                return value?.ToString();

            // Try case-insensitive lookup
            var key = evt.Data.Keys.FirstOrDefault(k => k.Equals(dataField, StringComparison.OrdinalIgnoreCase));
            if (key != null)
                return evt.Data[key]?.ToString();

            return null;
        }

        private bool MatchesOperator(string fieldValue, string op, string compareValue)
        {
            switch (op?.ToLower())
            {
                case "equals":
                    return string.Equals(fieldValue, compareValue, StringComparison.OrdinalIgnoreCase);

                case "contains":
                    return fieldValue.IndexOf(compareValue, StringComparison.OrdinalIgnoreCase) >= 0;

                case "regex":
                    try
                    {
                        return Regex.IsMatch(fieldValue, compareValue, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
                    }
                    catch
                    {
                        return false;
                    }

                case "gt":
                    return double.TryParse(fieldValue, out var gt1) && double.TryParse(compareValue, out var gt2) && gt1 > gt2;

                case "lt":
                    return double.TryParse(fieldValue, out var lt1) && double.TryParse(compareValue, out var lt2) && lt1 < lt2;

                case "gte":
                    return double.TryParse(fieldValue, out var gte1) && double.TryParse(compareValue, out var gte2) && gte1 >= gte2;

                case "lte":
                    return double.TryParse(fieldValue, out var lte1) && double.TryParse(compareValue, out var lte2) && lte1 <= lte2;

                case "exists":
                    return !string.IsNullOrEmpty(fieldValue);

                default:
                    return false;
            }
        }
    }
}
