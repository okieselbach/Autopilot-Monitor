using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Service for managing gather rules (what data the agent should collect)
    /// Merges global built-in rules with tenant-specific overrides
    /// </summary>
    public class GatherRuleService
    {
        private readonly TableStorageService _storageService;
        private readonly ILogger<GatherRuleService> _logger;
        private bool _seeded = false;

        public GatherRuleService(TableStorageService storageService, ILogger<GatherRuleService> logger)
        {
            _storageService = storageService;
            _logger = logger;
        }

        /// <summary>
        /// Gets all active gather rules for a tenant
        /// Merges global built-in rules with tenant-specific overrides/custom rules
        /// </summary>
        public async Task<List<GatherRule>> GetActiveRulesForTenantAsync(string tenantId)
        {
            await EnsureBuiltInRulesSeededAsync();

            // Get global built-in rules
            var globalRules = await _storageService.GetGatherRulesAsync("global");

            // Get tenant-specific rules (overrides + custom)
            var tenantRules = await _storageService.GetGatherRulesAsync(tenantId);

            // Create lookup of tenant overrides by RuleId
            var tenantOverrides = tenantRules.ToDictionary(r => r.RuleId, r => r);

            var mergedRules = new List<GatherRule>();

            // Merge: tenant override takes precedence over global
            foreach (var globalRule in globalRules)
            {
                if (tenantOverrides.TryGetValue(globalRule.RuleId, out var tenantOverride))
                {
                    // Tenant has an override for this rule (e.g., disabled it)
                    mergedRules.Add(tenantOverride);
                    tenantOverrides.Remove(globalRule.RuleId);
                }
                else
                {
                    // Use global rule as-is
                    mergedRules.Add(globalRule);
                }
            }

            // Add remaining tenant-specific custom rules
            foreach (var customRule in tenantOverrides.Values)
            {
                mergedRules.Add(customRule);
            }

            // Return only enabled rules
            return mergedRules.Where(r => r.Enabled).ToList();
        }

        /// <summary>
        /// Gets all gather rules for a tenant (including disabled) for portal display
        /// </summary>
        public async Task<List<GatherRule>> GetAllRulesForTenantAsync(string tenantId)
        {
            await EnsureBuiltInRulesSeededAsync();

            var globalRules = await _storageService.GetGatherRulesAsync("global");
            var tenantRules = await _storageService.GetGatherRulesAsync(tenantId);
            var tenantOverrides = tenantRules.ToDictionary(r => r.RuleId, r => r);

            var mergedRules = new List<GatherRule>();

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
        /// Creates a custom gather rule for a tenant
        /// </summary>
        public async Task<bool> CreateRuleAsync(string tenantId, GatherRule rule)
        {
            rule.IsBuiltIn = false;
            rule.CreatedAt = DateTime.UtcNow;
            rule.UpdatedAt = DateTime.UtcNow;
            return await _storageService.StoreGatherRuleAsync(rule, tenantId);
        }

        /// <summary>
        /// Updates a gather rule (enable/disable or modify)
        /// For built-in rules, creates a tenant override
        /// </summary>
        public async Task<bool> UpdateRuleAsync(string tenantId, GatherRule rule)
        {
            rule.UpdatedAt = DateTime.UtcNow;
            return await _storageService.StoreGatherRuleAsync(rule, tenantId);
        }

        /// <summary>
        /// Deletes a custom gather rule (cannot delete built-in rules)
        /// </summary>
        public async Task<bool> DeleteRuleAsync(string tenantId, string ruleId)
        {
            return await _storageService.DeleteGatherRuleAsync(tenantId, ruleId);
        }

        /// <summary>
        /// Seeds built-in gather rules if not already done.
        /// Also updates existing built-in rules when the code definitions change.
        /// </summary>
        private async Task EnsureBuiltInRulesSeededAsync()
        {
            if (_seeded) return;

            var existingRules = await _storageService.GetGatherRulesAsync("global");
            var builtInRules = BuiltInGatherRules.GetAll();

            if (existingRules.Count == 0)
            {
                _logger.LogInformation("Seeding built-in gather rules...");
                foreach (var rule in builtInRules)
                {
                    await _storageService.StoreGatherRuleAsync(rule, "global");
                }
                _logger.LogInformation($"Seeded {builtInRules.Count} built-in gather rules");
            }
            else
            {
                // Update existing built-in rules to pick up code changes (e.g. fixed Target values)
                var existingLookup = existingRules.ToDictionary(r => r.RuleId, r => r);
                var updated = 0;

                foreach (var rule in builtInRules)
                {
                    if (existingLookup.TryGetValue(rule.RuleId, out var existing))
                    {
                        if (existing.Target != rule.Target || existing.Description != rule.Description
                            || existing.Title != rule.Title || existing.CollectorType != rule.CollectorType)
                        {
                            await _storageService.StoreGatherRuleAsync(rule, "global");
                            updated++;
                        }
                    }
                    else
                    {
                        // New built-in rule added in code
                        await _storageService.StoreGatherRuleAsync(rule, "global");
                        updated++;
                    }
                }

                if (updated > 0)
                {
                    _logger.LogInformation($"Updated {updated} built-in gather rules from code definitions");
                }
            }

            _seeded = true;
        }
    }
}
