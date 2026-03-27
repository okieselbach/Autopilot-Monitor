using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Repository for rule definitions, results, and patterns.
    /// Covers: RuleResults, GatherRules, AnalyzeRules, ImeLogPatterns, RuleStates tables.
    /// </summary>
    public interface IRuleRepository
    {
        // --- Rule Results ---
        Task<bool> StoreRuleResultAsync(RuleResult result);
        Task<List<RuleResult>> GetRuleResultsAsync(string tenantId, string sessionId);

        // --- Gather Rules ---
        Task<bool> StoreGatherRuleAsync(GatherRule rule, string tenantId = "global");
        Task<List<GatherRule>> GetGatherRulesAsync(string partitionKey);
        Task<bool> DeleteGatherRuleAsync(string tenantId, string ruleId);

        // --- Rule States ---
        Task<bool> StoreRuleStateAsync(string tenantId, string ruleId, bool enabled);
        Task<Dictionary<string, bool>> GetRuleStatesAsync(string tenantId);
        Task<bool> DeleteRuleStateAsync(string tenantId, string ruleId);

        // --- Analyze Rules ---
        Task<bool> StoreAnalyzeRuleAsync(AnalyzeRule rule, string tenantId = "global");
        Task<List<AnalyzeRule>> GetAnalyzeRulesAsync(string partitionKey);
        Task<bool> DeleteAnalyzeRuleAsync(string tenantId, string ruleId);

        // --- IME Log Patterns ---
        Task<bool> StoreImeLogPatternAsync(ImeLogPattern pattern, string tenantId = "global");
        Task<List<ImeLogPattern>> GetImeLogPatternsAsync(string partitionKey);
        Task<bool> DeleteImeLogPatternAsync(string tenantId, string patternId);
    }
}
