using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Service for managing analyze rules (how to interpret collected events)
    /// Merges global built-in/community rules with tenant-specific custom rules.
    /// Enabled/disabled state for built-in and community rules is stored separately
    /// in the RuleStates table, so rule definitions can be updated centrally without
    /// losing per-tenant enabled/disabled preferences.
    /// </summary>
    public class AnalyzeRuleService
    {
        private readonly IRuleRepository _ruleRepo;
        private readonly ILogger<AnalyzeRuleService> _logger;
        private bool _seeded = false;

        public AnalyzeRuleService(IRuleRepository ruleRepo, ILogger<AnalyzeRuleService> logger)
        {
            _ruleRepo = ruleRepo;
            _logger = logger;
        }

        /// <summary>
        /// Gets all active analyze rules for a tenant (enabled only)
        /// </summary>
        public async Task<List<AnalyzeRule>> GetActiveRulesForTenantAsync(string tenantId)
        {
            var rules = await GetAllRulesForTenantAsync(tenantId);
            return rules.Where(r => r.Enabled).ToList();
        }

        /// <summary>
        /// Gets all analyze rules for a tenant (including disabled) for portal display
        /// </summary>
        public async Task<List<AnalyzeRule>> GetAllRulesForTenantAsync(string tenantId)
        {
            await EnsureBuiltInRulesSeededAsync();

            // Global rules: built-in + community (single source of truth for definitions)
            var globalRules = await _ruleRepo.GetAnalyzeRulesAsync("global");

            // Tenant rules: only custom rules (IsBuiltIn=false, IsCommunity=false)
            var tenantRules = await _ruleRepo.GetAnalyzeRulesAsync(tenantId);
            var customRules = tenantRules.Where(r => !r.IsBuiltIn && !r.IsCommunity).ToList();

            // Per-tenant enabled/disabled states for global rules
            var ruleStates = await _ruleRepo.GetRuleStatesAsync(tenantId);

            var mergedRules = new List<AnalyzeRule>();

            // Apply tenant state overrides to global rules
            foreach (var rule in globalRules)
            {
                if (ruleStates.TryGetValue(rule.RuleId, out var state))
                {
                    rule.Enabled = state.Enabled;
                    rule.MarkSessionAsFailed = state.MarkSessionAsFailed;
                }
                mergedRules.Add(rule);
            }

            // Tenant custom rules carry their own MarkSessionAsFailedDefault; no override needed
            // since the tenant already fully owns the rule definition.
            mergedRules.AddRange(customRules);

            return mergedRules;
        }

        /// <summary>
        /// Creates a custom analyze rule for a tenant.
        /// Throws if a rule with the same ID already exists (global or tenant partition).
        /// Uses point queries (O(1)) instead of loading all rules.
        /// </summary>
        public async Task<bool> CreateRuleAsync(string tenantId, AnalyzeRule rule)
        {
            if (await _ruleRepo.AnalyzeRuleExistsAsync("global", rule.RuleId)
                || await _ruleRepo.AnalyzeRuleExistsAsync(tenantId, rule.RuleId))
            {
                throw new InvalidOperationException($"A rule with ID '{rule.RuleId}' already exists.");
            }

            rule.IsBuiltIn = false;
            rule.IsCommunity = false;
            rule.CreatedAt = DateTime.UtcNow;
            rule.UpdatedAt = DateTime.UtcNow;
            return await _ruleRepo.StoreAnalyzeRuleAsync(rule, tenantId);
        }

        /// <summary>
        /// Updates an analyze rule.
        /// For built-in/community rules: only the enabled/disabled state is stored (per tenant).
        /// For custom rules: the full rule is updated in the tenant partition.
        /// </summary>
        public async Task<bool> UpdateRuleAsync(string tenantId, AnalyzeRule rule)
        {
            if (rule.IsBuiltIn || rule.IsCommunity)
            {
                var state = new RuleState
                {
                    Enabled = rule.Enabled,
                    MarkSessionAsFailed = rule.MarkSessionAsFailed
                };
                return await _ruleRepo.StoreRuleStateAsync(tenantId, rule.RuleId, state);
            }

            rule.UpdatedAt = DateTime.UtcNow;
            return await _ruleRepo.StoreAnalyzeRuleAsync(rule, tenantId);
        }

        /// <summary>
        /// Creates a tenant custom rule from a template rule, substituting template variables
        /// with tenant-specific values. The original template remains disabled for the tenant.
        /// </summary>
        public async Task<AnalyzeRule> CreateFromTemplateAsync(
            string tenantId,
            string templateRuleId,
            Dictionary<string, string> variableValues)
        {
            var allRules = await GetAllRulesForTenantAsync(tenantId);
            var template = allRules.FirstOrDefault(r => r.RuleId == templateRuleId);

            if (template == null)
                throw new InvalidOperationException($"Rule '{templateRuleId}' not found.");

            if (template.TemplateVariables == null || template.TemplateVariables.Count == 0)
                throw new InvalidOperationException($"Rule '{templateRuleId}' is not a template rule.");

            // Check if a custom copy already exists for this tenant (by lineage)
            var existingCopy = allRules.FirstOrDefault(r => r.DerivedFromTemplateRuleId == templateRuleId);
            if (existingCopy != null)
                throw new InvalidOperationException($"A custom copy of '{templateRuleId}' already exists: '{existingCopy.RuleId}'.");

            // Check for RuleId collision via point query (e.g., someone manually created a rule with the same ID)
            var targetRuleId = $"{templateRuleId}-CUSTOM";
            if (await _ruleRepo.AnalyzeRuleExistsAsync("global", targetRuleId)
                || await _ruleRepo.AnalyzeRuleExistsAsync(tenantId, targetRuleId))
                throw new InvalidOperationException($"A rule with ID '{targetRuleId}' already exists. Delete or rename it first.");

            // Validate all template variables have values
            foreach (var tv in template.TemplateVariables)
            {
                if (!variableValues.TryGetValue(tv.Name, out var val) || string.IsNullOrWhiteSpace(val))
                    throw new ArgumentException($"Missing required value for template variable '{tv.Name}'.");
            }

            // Deep-clone the template
            var customRule = JsonConvert.DeserializeObject<AnalyzeRule>(
                JsonConvert.SerializeObject(template))
                ?? throw new InvalidOperationException("Failed to clone template rule.");

            customRule.RuleId = $"{templateRuleId}-CUSTOM";
            customRule.IsBuiltIn = false;
            customRule.IsCommunity = false;
            customRule.Enabled = true;
            customRule.DerivedFromTemplateRuleId = templateRuleId;
            customRule.TemplateVariables = new List<TemplateVariable>();
            customRule.CreatedAt = DateTime.UtcNow;
            customRule.UpdatedAt = DateTime.UtcNow;

            // Substitute variable values into conditions
            foreach (var tv in template.TemplateVariables)
            {
                var userValue = variableValues[tv.Name];

                if (tv.ConditionIndex < 0 || tv.ConditionIndex >= customRule.Conditions.Count)
                {
                    _logger.LogWarning("Template variable '{Name}' has invalid conditionIndex {Index}", tv.Name, tv.ConditionIndex);
                    continue;
                }

                var condition = customRule.Conditions[tv.ConditionIndex];
                switch (tv.Field?.ToLowerInvariant())
                {
                    case "value": condition.Value = userValue; break;
                    case "eventtype": condition.EventType = userValue; break;
                    case "datafield": condition.DataField = userValue; break;
                    case "eventafiltervalue": condition.EventAFilterValue = userValue; break;
                    default:
                        _logger.LogWarning("Template variable '{Name}' has unknown field '{Field}'", tv.Name, tv.Field);
                        break;
                }
            }

            // Store the custom rule in the tenant partition
            var success = await _ruleRepo.StoreAnalyzeRuleAsync(customRule, tenantId);
            if (!success)
                throw new InvalidOperationException("Failed to store custom rule.");

            // Ensure the template rule is disabled for this tenant
            await _ruleRepo.StoreRuleStateAsync(tenantId, templateRuleId, new RuleState { Enabled = false });

            _logger.LogInformation("Created custom rule '{CustomRuleId}' from template '{TemplateRuleId}' for tenant '{TenantId}'",
                customRule.RuleId, templateRuleId, tenantId);

            return customRule;
        }

        /// <summary>
        /// Deletes an analyze rule.
        /// For built-in/community rules: removes the tenant's state override (resets to rule default).
        /// For custom rules: deletes the rule from the tenant partition.
        /// </summary>
        public async Task<bool> DeleteRuleAsync(string tenantId, AnalyzeRule rule)
        {
            if (rule.IsBuiltIn || rule.IsCommunity)
            {
                return await _ruleRepo.DeleteRuleStateAsync(tenantId, rule.RuleId);
            }

            return await _ruleRepo.DeleteAnalyzeRuleAsync(tenantId, rule.RuleId);
        }

        /// <summary>
        /// Re-imports all built-in analyze rules into the global partition.
        /// Deletes old global built-in rules and writes current code definitions.
        /// Tenant RuleStates are preserved.
        /// </summary>
        public async Task<(int deleted, int written)> ReseedBuiltInRulesAsync()
        {
            _logger.LogInformation("Reseeding built-in analyze rules (full re-import)...");

            var existingGlobalRules = await _ruleRepo.GetAnalyzeRulesAsync("global");

            var deleted = 0;
            foreach (var rule in existingGlobalRules.Where(r => r.IsBuiltIn))
            {
                await _ruleRepo.DeleteAnalyzeRuleAsync("global", rule.RuleId);
                deleted++;
            }
            _logger.LogInformation($"Deleted {deleted} old global built-in analyze rules");

            var builtInRules = BuiltInAnalyzeRules.GetAll();
            foreach (var rule in builtInRules)
            {
                await _ruleRepo.StoreAnalyzeRuleAsync(rule, "global");
            }
            _logger.LogInformation($"Written {builtInRules.Count} built-in analyze rules from code");

            _seeded = false;

            return (deleted, builtInRules.Count);
        }

        /// <summary>
        /// Seeds built-in analyze rules if not already done.
        /// Also updates existing built-in rules when the code definitions change (version or key fields).
        /// </summary>
        private async Task EnsureBuiltInRulesSeededAsync()
        {
            if (_seeded) return;

            var existingRules = await _ruleRepo.GetAnalyzeRulesAsync("global");
            var builtInRules = BuiltInAnalyzeRules.GetAll();

            if (existingRules.Count == 0)
            {
                _logger.LogInformation("Seeding built-in analyze rules...");
                foreach (var rule in builtInRules)
                {
                    await _ruleRepo.StoreAnalyzeRuleAsync(rule, "global");
                }
                _logger.LogInformation($"Seeded {builtInRules.Count} built-in analyze rules");
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
                            || existing.Title != rule.Title
                            || existing.Description != rule.Description
                            || existing.Severity != rule.Severity
                            || existing.Trigger != rule.Trigger)
                        {
                            await _ruleRepo.StoreAnalyzeRuleAsync(rule, "global");
                            updated++;
                        }
                    }
                    else
                    {
                        // New built-in rule added in code
                        await _ruleRepo.StoreAnalyzeRuleAsync(rule, "global");
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
