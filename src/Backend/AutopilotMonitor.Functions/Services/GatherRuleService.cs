using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;
using System.Linq;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Service for managing gather rules (what data the agent should collect)
    /// Merges global built-in/community rules with tenant-specific custom rules.
    /// Enabled/disabled state for built-in and community rules is stored separately
    /// in the RuleStates table, so rule definitions can be updated centrally without
    /// losing per-tenant enabled/disabled preferences.
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
        /// Gets all active gather rules for a tenant (enabled only)
        /// </summary>
        public async Task<List<GatherRule>> GetActiveRulesForTenantAsync(string tenantId)
        {
            var rules = await GetAllRulesForTenantAsync(tenantId);
            return rules.Where(r => r.Enabled).ToList();
        }

        /// <summary>
        /// Gets all gather rules for a tenant (including disabled) for portal display
        /// </summary>
        public async Task<List<GatherRule>> GetAllRulesForTenantAsync(string tenantId)
        {
            await EnsureBuiltInRulesSeededAsync();

            // Global rules: built-in + community (single source of truth for definitions)
            var globalRules = await _storageService.GetGatherRulesAsync("global");

            // Tenant rules: only custom rules (IsBuiltIn=false, IsCommunity=false)
            var tenantRules = await _storageService.GetGatherRulesAsync(tenantId);
            var customRules = tenantRules.Where(r => !r.IsBuiltIn && !r.IsCommunity).ToList();

            // Per-tenant enabled/disabled states for global rules
            var ruleStates = await _storageService.GetRuleStatesAsync(tenantId);

            var mergedRules = new List<GatherRule>();

            // Apply tenant state overrides to global rules
            foreach (var rule in globalRules)
            {
                if (ruleStates.TryGetValue(rule.RuleId, out var enabled))
                    rule.Enabled = enabled;
                mergedRules.Add(rule);
            }

            // Add tenant-specific custom rules
            mergedRules.AddRange(customRules);

            return mergedRules;
        }

        /// <summary>
        /// Creates a custom gather rule for a tenant
        /// </summary>
        public async Task<bool> CreateRuleAsync(string tenantId, GatherRule rule)
        {
            rule.IsBuiltIn = false;
            rule.IsCommunity = false;
            rule.CreatedAt = DateTime.UtcNow;
            rule.UpdatedAt = DateTime.UtcNow;
            return await _storageService.StoreGatherRuleAsync(rule, tenantId);
        }

        /// <summary>
        /// Updates a gather rule.
        /// For built-in/community rules: only the enabled/disabled state is stored (per tenant).
        /// For custom rules: the full rule is updated in the tenant partition.
        /// </summary>
        public async Task<bool> UpdateRuleAsync(string tenantId, GatherRule rule)
        {
            if (rule.IsBuiltIn || rule.IsCommunity)
            {
                return await _storageService.StoreRuleStateAsync(tenantId, rule.RuleId, rule.Enabled);
            }

            rule.UpdatedAt = DateTime.UtcNow;
            return await _storageService.StoreGatherRuleAsync(rule, tenantId);
        }

        /// <summary>
        /// Deletes a gather rule.
        /// For built-in/community rules: removes the tenant's state override (resets to rule default).
        /// For custom rules: deletes the rule from the tenant partition.
        /// </summary>
        public async Task<bool> DeleteRuleAsync(string tenantId, GatherRule rule)
        {
            if (rule.IsBuiltIn || rule.IsCommunity)
            {
                return await _storageService.DeleteRuleStateAsync(tenantId, rule.RuleId);
            }

            return await _storageService.DeleteGatherRuleAsync(tenantId, rule.RuleId);
        }

        /// <summary>
        /// Re-imports all built-in gather rules into the global partition.
        /// Deletes old global built-in rules and writes current code definitions.
        /// Tenant RuleStates are preserved.
        /// </summary>
        public async Task<(int deleted, int written)> ReseedBuiltInRulesAsync()
        {
            _logger.LogInformation("Reseeding built-in gather rules (full re-import)...");

            var existingGlobalRules = await _storageService.GetGatherRulesAsync("global");

            var deleted = 0;
            foreach (var rule in existingGlobalRules.Where(r => r.IsBuiltIn))
            {
                await _storageService.DeleteGatherRuleAsync("global", rule.RuleId);
                deleted++;
            }
            _logger.LogInformation($"Deleted {deleted} old global built-in gather rules");

            var builtInRules = BuiltInGatherRules.GetAll();
            foreach (var rule in builtInRules)
            {
                await _storageService.StoreGatherRuleAsync(rule, "global");
            }
            _logger.LogInformation($"Written {builtInRules.Count} built-in gather rules from code");

            _seeded = false;

            return (deleted, builtInRules.Count);
        }

        /// <summary>
        /// Seeds built-in gather rules if not already done.
        /// Also updates existing built-in rules when the code definitions change (version or key fields).
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
                var existingLookup = existingRules.ToDictionary(r => r.RuleId, r => r);
                var updated = 0;

                foreach (var rule in builtInRules)
                {
                    if (existingLookup.TryGetValue(rule.RuleId, out var existing))
                    {
                        if (existing.Version != rule.Version
                            || existing.Target != rule.Target
                            || existing.Description != rule.Description
                            || existing.Title != rule.Title
                            || existing.CollectorType != rule.CollectorType
                            || existing.Trigger != rule.Trigger
                            || existing.TriggerPhase != rule.TriggerPhase)
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
