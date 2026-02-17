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
        /// Updates a built-in analyze rule globally (Galactic Admin only)
        /// Modifies the global definition that all tenants inherit
        /// </summary>
        public async Task<bool> UpdateGlobalRuleAsync(AnalyzeRule rule)
        {
            rule.UpdatedAt = DateTime.UtcNow;
            rule.IsBuiltIn = true;
            return await _storageService.StoreAnalyzeRuleAsync(rule, "global");
        }

        /// <summary>
        /// Deletes a custom analyze rule (cannot delete built-in rules)
        /// </summary>
        public async Task<bool> DeleteRuleAsync(string tenantId, string ruleId)
        {
            return await _storageService.DeleteAnalyzeRuleAsync(tenantId, ruleId);
        }

        /// <summary>
        /// Re-imports all built-in analyze rules into the global partition.
        /// Deletes old global built-in rules and writes current code definitions.
        /// </summary>
        public async Task<(int deleted, int written)> ReseedBuiltInRulesAsync()
        {
            _logger.LogInformation("Reseeding built-in analyze rules (full re-import)...");

            // 1. Get existing global rules
            var existingGlobalRules = await _storageService.GetAnalyzeRulesAsync("global");

            // 2. Delete all existing global built-in rules
            var deleted = 0;
            foreach (var rule in existingGlobalRules.Where(r => r.IsBuiltIn))
            {
                await _storageService.DeleteAnalyzeRuleAsync("global", rule.RuleId);
                deleted++;
            }
            _logger.LogInformation($"Deleted {deleted} old global built-in rules");

            // 3. Write current code definitions
            var builtInRules = BuiltInAnalyzeRules.GetAll();
            foreach (var rule in builtInRules)
            {
                await _storageService.StoreAnalyzeRuleAsync(rule, "global");
            }
            _logger.LogInformation($"Written {builtInRules.Count} built-in analyze rules from code");

            // Reset seed flag so next request picks up fresh data
            _seeded = false;

            return (deleted, builtInRules.Count);
        }

        /// <summary>
        /// Seeds built-in analyze rules if not already done.
        /// Also updates existing built-in rules when code definitions change.
        /// </summary>
        private async Task EnsureBuiltInRulesSeededAsync()
        {
            if (_seeded) return;

            var existingRules = await _storageService.GetAnalyzeRulesAsync("global");
            var builtInRules = BuiltInAnalyzeRules.GetAll();

            if (existingRules.Count == 0)
            {
                _logger.LogInformation("Seeding built-in analyze rules...");
                foreach (var rule in builtInRules)
                {
                    await _storageService.StoreAnalyzeRuleAsync(rule, "global");
                }
                _logger.LogInformation($"Seeded {builtInRules.Count} built-in analyze rules");
            }
            else
            {
                // Update existing built-in rules to pick up code changes and add new rules
                var existingLookup = existingRules.ToDictionary(r => r.RuleId, r => r);
                var updated = 0;

                foreach (var rule in builtInRules)
                {
                    if (existingLookup.TryGetValue(rule.RuleId, out var existing))
                    {
                        if (existing.Title != rule.Title || existing.Description != rule.Description
                            || existing.Severity != rule.Severity || existing.Trigger != rule.Trigger)
                        {
                            await _storageService.StoreAnalyzeRuleAsync(rule, "global");
                            updated++;
                        }
                    }
                    else
                    {
                        // New built-in rule added in code (e.g. correlation rules)
                        await _storageService.StoreAnalyzeRuleAsync(rule, "global");
                        updated++;
                    }
                }

                if (updated > 0)
                {
                    _logger.LogInformation($"Updated {updated} built-in analyze rules from code definitions");
                }
            }

            _seeded = true;
        }
    }
}
