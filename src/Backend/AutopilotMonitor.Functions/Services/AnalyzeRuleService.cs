using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Service for managing analyze rules (how to interpret collected events)
    /// Merges global built-in rules with tenant-specific overrides
    /// </summary>
    public class AnalyzeRuleService
    {
        private readonly TableStorageService _storageService;
        private readonly ILogger<AnalyzeRuleService> _logger;
        private bool _seeded = false;

        public AnalyzeRuleService(TableStorageService storageService, ILogger<AnalyzeRuleService> logger)
        {
            _storageService = storageService;
            _logger = logger;
        }

        /// <summary>
        /// Gets all active analyze rules for a tenant
        /// Merges global built-in rules with tenant-specific overrides/custom rules
        /// </summary>
        public async Task<List<AnalyzeRule>> GetActiveRulesForTenantAsync(string tenantId)
        {
            await EnsureBuiltInRulesSeededAsync();

            var globalRules = await _storageService.GetAnalyzeRulesAsync("global");
            var tenantRules = await _storageService.GetAnalyzeRulesAsync(tenantId);
            var tenantOverrides = tenantRules.ToDictionary(r => r.RuleId, r => r);

            var mergedRules = new List<AnalyzeRule>();

            foreach (var globalRule in globalRules)
            {
                if (tenantOverrides.TryGetValue(globalRule.RuleId, out var tenantOverride))
                {
                    mergedRules.Add(tenantOverride);
                    tenantOverrides.Remove(globalRule.RuleId);
                }
                else
                {
                    mergedRules.Add(globalRule);
                }
            }

            foreach (var customRule in tenantOverrides.Values)
            {
                mergedRules.Add(customRule);
            }

            return mergedRules.Where(r => r.Enabled).ToList();
        }

        /// <summary>
        /// Gets all analyze rules for a tenant (including disabled) for portal display
        /// </summary>
        public async Task<List<AnalyzeRule>> GetAllRulesForTenantAsync(string tenantId)
        {
            await EnsureBuiltInRulesSeededAsync();

            var globalRules = await _storageService.GetAnalyzeRulesAsync("global");
            var tenantRules = await _storageService.GetAnalyzeRulesAsync(tenantId);
            var tenantOverrides = tenantRules.ToDictionary(r => r.RuleId, r => r);

            var mergedRules = new List<AnalyzeRule>();

            foreach (var globalRule in globalRules)
            {
                if (tenantOverrides.TryGetValue(globalRule.RuleId, out var tenantOverride))
                {
                    mergedRules.Add(tenantOverride);
                    tenantOverrides.Remove(globalRule.RuleId);
                }
                else
                {
                    mergedRules.Add(globalRule);
                }
            }

            foreach (var customRule in tenantOverrides.Values)
            {
                mergedRules.Add(customRule);
            }

            return mergedRules;
        }

        /// <summary>
        /// Creates a custom analyze rule for a tenant
        /// </summary>
        public async Task<bool> CreateRuleAsync(string tenantId, AnalyzeRule rule)
        {
            rule.IsBuiltIn = false;
            rule.CreatedAt = DateTime.UtcNow;
            rule.UpdatedAt = DateTime.UtcNow;
            return await _storageService.StoreAnalyzeRuleAsync(rule, tenantId);
        }

        /// <summary>
        /// Updates an analyze rule (enable/disable or modify)
        /// For built-in rules, creates a tenant override
        /// </summary>
        public async Task<bool> UpdateRuleAsync(string tenantId, AnalyzeRule rule)
        {
            rule.UpdatedAt = DateTime.UtcNow;
            return await _storageService.StoreAnalyzeRuleAsync(rule, tenantId);
        }

        /// <summary>
        /// Deletes a custom analyze rule (cannot delete built-in rules)
        /// </summary>
        public async Task<bool> DeleteRuleAsync(string tenantId, string ruleId)
        {
            return await _storageService.DeleteAnalyzeRuleAsync(tenantId, ruleId);
        }

        /// <summary>
        /// Seeds built-in analyze rules if not already done
        /// </summary>
        private async Task EnsureBuiltInRulesSeededAsync()
        {
            if (_seeded) return;

            var existingRules = await _storageService.GetAnalyzeRulesAsync("global");
            if (existingRules.Count > 0)
            {
                _seeded = true;
                return;
            }

            _logger.LogInformation("Seeding built-in analyze rules...");

            var builtInRules = BuiltInAnalyzeRules.GetAll();
            foreach (var rule in builtInRules)
            {
                await _storageService.StoreAnalyzeRuleAsync(rule, "global");
            }

            _seeded = true;
            _logger.LogInformation($"Seeded {builtInRules.Count} built-in analyze rules");
        }
    }
}
