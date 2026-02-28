using Azure.Data.Tables;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services
{
    public partial class TableStorageService
    {
        // ===== RULE RESULTS METHODS =====

        /// <summary>
        /// Stores a rule evaluation result
        /// PartitionKey: {TenantId}_{SessionId}, RowKey: RuleId
        /// </summary>
        public async Task<bool> StoreRuleResultAsync(RuleResult result)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.RuleResults);
                var partitionKey = $"{result.TenantId}_{result.SessionId}";

                var entity = new TableEntity(partitionKey, result.RuleId)
                {
                    ["ResultId"] = result.ResultId,
                    ["SessionId"] = result.SessionId,
                    ["TenantId"] = result.TenantId,
                    ["RuleId"] = result.RuleId,
                    ["RuleTitle"] = result.RuleTitle ?? string.Empty,
                    ["Severity"] = result.Severity ?? string.Empty,
                    ["Category"] = result.Category ?? string.Empty,
                    ["ConfidenceScore"] = result.ConfidenceScore,
                    ["Explanation"] = result.Explanation ?? string.Empty,
                    ["RemediationJson"] = JsonConvert.SerializeObject(result.Remediation ?? new List<RemediationStep>()),
                    ["RelatedDocsJson"] = JsonConvert.SerializeObject(result.RelatedDocs ?? new List<RelatedDoc>()),
                    ["MatchedConditionsJson"] = JsonConvert.SerializeObject(result.MatchedConditions ?? new Dictionary<string, object>()),
                    ["DetectedAt"] = result.DetectedAt
                };

                await tableClient.UpsertEntityAsync(entity);
                _logger.LogInformation($"Stored rule result {result.RuleId} for session {result.SessionId}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to store rule result {result.RuleId}");
                return false;
            }
        }

        /// <summary>
        /// Gets all rule results for a session
        /// </summary>
        public async Task<List<RuleResult>> GetRuleResultsAsync(string tenantId, string sessionId)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.RuleResults);
                var partitionKey = $"{tenantId}_{sessionId}";
                var query = tableClient.QueryAsync<TableEntity>(filter: $"PartitionKey eq '{partitionKey}'");

                var results = new List<RuleResult>();
                await foreach (var entity in query)
                {
                    results.Add(new RuleResult
                    {
                        ResultId = entity.GetString("ResultId") ?? string.Empty,
                        SessionId = entity.GetString("SessionId") ?? string.Empty,
                        TenantId = entity.GetString("TenantId") ?? string.Empty,
                        RuleId = entity.GetString("RuleId") ?? entity.RowKey,
                        RuleTitle = entity.GetString("RuleTitle") ?? string.Empty,
                        Severity = entity.GetString("Severity") ?? string.Empty,
                        Category = entity.GetString("Category") ?? string.Empty,
                        ConfidenceScore = entity.GetInt32("ConfidenceScore") ?? 0,
                        Explanation = entity.GetString("Explanation") ?? string.Empty,
                        Remediation = DeserializeJson<List<RemediationStep>>(entity.GetString("RemediationJson")),
                        RelatedDocs = DeserializeJson<List<RelatedDoc>>(entity.GetString("RelatedDocsJson")),
                        MatchedConditions = DeserializeMatchedConditions(entity.GetString("MatchedConditionsJson")),
                        DetectedAt = entity.GetDateTimeOffset("DetectedAt")?.UtcDateTime ?? DateTime.UtcNow
                    });
                }

                return results.OrderByDescending(r => r.ConfidenceScore).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get rule results for session {sessionId}");
                return new List<RuleResult>();
            }
        }

        // ===== GATHER RULES METHODS =====

        /// <summary>
        /// Stores or updates a gather rule
        /// PartitionKey: TenantId (or "global" for built-in), RowKey: RuleId
        /// </summary>
        public async Task<bool> StoreGatherRuleAsync(GatherRule rule, string tenantId = "global")
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.GatherRules);

                var entity = new TableEntity(tenantId, rule.RuleId)
                {
                    ["Title"] = rule.Title ?? string.Empty,
                    ["Description"] = rule.Description ?? string.Empty,
                    ["Category"] = rule.Category ?? string.Empty,
                    ["Version"] = rule.Version ?? "1.0.0",
                    ["Author"] = rule.Author ?? "Autopilot Monitor",
                    ["Enabled"] = rule.Enabled,
                    ["IsBuiltIn"] = rule.IsBuiltIn,
                    ["IsCommunity"] = rule.IsCommunity,
                    ["CollectorType"] = rule.CollectorType ?? string.Empty,
                    ["Target"] = rule.Target ?? string.Empty,
                    ["ParametersJson"] = JsonConvert.SerializeObject(rule.Parameters ?? new Dictionary<string, string>()),
                    ["Trigger"] = rule.Trigger ?? string.Empty,
                    ["IntervalSeconds"] = rule.IntervalSeconds,
                    ["TriggerPhase"] = rule.TriggerPhase ?? string.Empty,
                    ["TriggerEventType"] = rule.TriggerEventType ?? string.Empty,
                    ["OutputEventType"] = rule.OutputEventType ?? string.Empty,
                    ["OutputSeverity"] = rule.OutputSeverity ?? "Info",
                    ["TagsJson"] = JsonConvert.SerializeObject(rule.Tags ?? new string[0]),
                    ["CreatedAt"] = rule.CreatedAt,
                    ["UpdatedAt"] = rule.UpdatedAt
                };

                await tableClient.UpsertEntityAsync(entity);
                _logger.LogDebug($"Stored gather rule {rule.RuleId} for {tenantId}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to store gather rule {rule.RuleId}");
                return false;
            }
        }

        /// <summary>
        /// Gets gather rules for a partition (tenant or "global")
        /// </summary>
        public async Task<List<GatherRule>> GetGatherRulesAsync(string partitionKey)
        {
            if (partitionKey != "global")
                SecurityValidator.EnsureValidGuid(partitionKey, nameof(partitionKey));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.GatherRules);
                var query = tableClient.QueryAsync<TableEntity>(filter: $"PartitionKey eq '{partitionKey}'");

                var rules = new List<GatherRule>();
                await foreach (var entity in query)
                {
                    rules.Add(MapToGatherRule(entity));
                }

                return rules;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get gather rules for {partitionKey}");
                return new List<GatherRule>();
            }
        }

        /// <summary>
        /// Deletes a gather rule
        /// </summary>
        public async Task<bool> DeleteGatherRuleAsync(string tenantId, string ruleId)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.GatherRules);
                await tableClient.DeleteEntityAsync(tenantId, ruleId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to delete gather rule {ruleId}");
                return false;
            }
        }

        private GatherRule MapToGatherRule(TableEntity entity)
        {
            return new GatherRule
            {
                RuleId = entity.RowKey,
                Title = entity.GetString("Title") ?? string.Empty,
                Description = entity.GetString("Description") ?? string.Empty,
                Category = entity.GetString("Category") ?? string.Empty,
                Version = entity.GetString("Version") ?? "1.0.0",
                Author = entity.GetString("Author") ?? "Autopilot Monitor",
                Enabled = entity.GetBoolean("Enabled") ?? true,
                IsBuiltIn = entity.GetBoolean("IsBuiltIn") ?? false,
                IsCommunity = entity.GetBoolean("IsCommunity") ?? false,
                CollectorType = entity.GetString("CollectorType") ?? string.Empty,
                Target = entity.GetString("Target") ?? string.Empty,
                Parameters = DeserializeJson<Dictionary<string, string>>(entity.GetString("ParametersJson")),
                Trigger = entity.GetString("Trigger") ?? string.Empty,
                IntervalSeconds = entity.GetInt32("IntervalSeconds"),
                TriggerPhase = entity.GetString("TriggerPhase") ?? string.Empty,
                TriggerEventType = entity.GetString("TriggerEventType") ?? string.Empty,
                OutputEventType = entity.GetString("OutputEventType") ?? string.Empty,
                OutputSeverity = entity.GetString("OutputSeverity") ?? "Info",
                Tags = DeserializeJsonArray(entity.GetString("TagsJson")),
                CreatedAt = entity.GetDateTimeOffset("CreatedAt")?.UtcDateTime ?? DateTime.UtcNow,
                UpdatedAt = entity.GetDateTimeOffset("UpdatedAt")?.UtcDateTime ?? DateTime.UtcNow
            };
        }

        // ===== RULE STATES METHODS =====

        /// <summary>
        /// Stores or updates the enabled/disabled state for a built-in or community rule per tenant
        /// PartitionKey: TenantId, RowKey: RuleId
        /// </summary>
        public async Task<bool> StoreRuleStateAsync(string tenantId, string ruleId, bool enabled)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.RuleStates);

                var entity = new TableEntity(tenantId, ruleId)
                {
                    ["Enabled"] = enabled,
                    ["UpdatedAt"] = DateTime.UtcNow
                };

                await tableClient.UpsertEntityAsync(entity);
                _logger.LogDebug($"Stored rule state {ruleId} for {tenantId}: enabled={enabled}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to store rule state {ruleId} for {tenantId}");
                return false;
            }
        }

        /// <summary>
        /// Gets all rule states for a tenant as a dictionary of ruleId → enabled
        /// </summary>
        public async Task<Dictionary<string, bool>> GetRuleStatesAsync(string tenantId)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.RuleStates);
                var query = tableClient.QueryAsync<TableEntity>(filter: $"PartitionKey eq '{tenantId}'");

                var states = new Dictionary<string, bool>();
                await foreach (var entity in query)
                {
                    states[entity.RowKey] = entity.GetBoolean("Enabled") ?? true;
                }

                return states;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get rule states for {tenantId}");
                return new Dictionary<string, bool>();
            }
        }

        /// <summary>
        /// Deletes the rule state for a tenant (resets to rule's default enabled state)
        /// </summary>
        public async Task<bool> DeleteRuleStateAsync(string tenantId, string ruleId)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.RuleStates);
                await tableClient.DeleteEntityAsync(tenantId, ruleId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to delete rule state {ruleId} for {tenantId}");
                return false;
            }
        }

        // ===== ANALYZE RULES METHODS =====

        /// <summary>
        /// Stores or updates an analyze rule
        /// PartitionKey: TenantId (or "global" for built-in), RowKey: RuleId
        /// </summary>
        public async Task<bool> StoreAnalyzeRuleAsync(AnalyzeRule rule, string tenantId = "global")
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.AnalyzeRules);

                var entity = new TableEntity(tenantId, rule.RuleId)
                {
                    ["Title"] = rule.Title ?? string.Empty,
                    ["Description"] = rule.Description ?? string.Empty,
                    ["Severity"] = rule.Severity ?? string.Empty,
                    ["Category"] = rule.Category ?? string.Empty,
                    ["Version"] = rule.Version ?? "1.0.0",
                    ["Author"] = rule.Author ?? "Autopilot Monitor",
                    ["Enabled"] = rule.Enabled,
                    ["IsBuiltIn"] = rule.IsBuiltIn,
                    ["IsCommunity"] = rule.IsCommunity,
                    ["Trigger"] = rule.Trigger ?? "single",
                    ["ConditionsJson"] = JsonConvert.SerializeObject(rule.Conditions ?? new List<RuleCondition>()),
                    ["BaseConfidence"] = rule.BaseConfidence,
                    ["ConfidenceFactorsJson"] = JsonConvert.SerializeObject(rule.ConfidenceFactors ?? new List<ConfidenceFactor>()),
                    ["ConfidenceThreshold"] = rule.ConfidenceThreshold,
                    ["Explanation"] = rule.Explanation ?? string.Empty,
                    ["RemediationJson"] = JsonConvert.SerializeObject(rule.Remediation ?? new List<RemediationStep>()),
                    ["RelatedDocsJson"] = JsonConvert.SerializeObject(rule.RelatedDocs ?? new List<RelatedDoc>()),
                    ["TagsJson"] = JsonConvert.SerializeObject(rule.Tags ?? new string[0]),
                    ["CreatedAt"] = rule.CreatedAt,
                    ["UpdatedAt"] = rule.UpdatedAt
                };

                await tableClient.UpsertEntityAsync(entity);
                _logger.LogDebug($"Stored analyze rule {rule.RuleId} for {tenantId}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to store analyze rule {rule.RuleId}");
                return false;
            }
        }

        /// <summary>
        /// Gets analyze rules for a partition (tenant or "global")
        /// </summary>
        public async Task<List<AnalyzeRule>> GetAnalyzeRulesAsync(string partitionKey)
        {
            if (partitionKey != "global")
                SecurityValidator.EnsureValidGuid(partitionKey, nameof(partitionKey));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.AnalyzeRules);
                var query = tableClient.QueryAsync<TableEntity>(filter: $"PartitionKey eq '{partitionKey}'");

                var rules = new List<AnalyzeRule>();
                await foreach (var entity in query)
                {
                    rules.Add(MapToAnalyzeRule(entity));
                }

                return rules;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get analyze rules for {partitionKey}");
                return new List<AnalyzeRule>();
            }
        }

        /// <summary>
        /// Deletes an analyze rule
        /// </summary>
        public async Task<bool> DeleteAnalyzeRuleAsync(string tenantId, string ruleId)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.AnalyzeRules);
                await tableClient.DeleteEntityAsync(tenantId, ruleId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to delete analyze rule {ruleId}");
                return false;
            }
        }

        private AnalyzeRule MapToAnalyzeRule(TableEntity entity)
        {
            return new AnalyzeRule
            {
                RuleId = entity.RowKey,
                Title = entity.GetString("Title") ?? string.Empty,
                Description = entity.GetString("Description") ?? string.Empty,
                Severity = entity.GetString("Severity") ?? string.Empty,
                Category = entity.GetString("Category") ?? string.Empty,
                Version = entity.GetString("Version") ?? "1.0.0",
                Author = entity.GetString("Author") ?? "Autopilot Monitor",
                Enabled = entity.GetBoolean("Enabled") ?? true,
                IsBuiltIn = entity.GetBoolean("IsBuiltIn") ?? false,
                IsCommunity = entity.GetBoolean("IsCommunity") ?? false,
                Trigger = entity.GetString("Trigger") ?? "single",
                Conditions = DeserializeJson<List<RuleCondition>>(entity.GetString("ConditionsJson")),
                BaseConfidence = entity.GetInt32("BaseConfidence") ?? 50,
                ConfidenceFactors = DeserializeJson<List<ConfidenceFactor>>(entity.GetString("ConfidenceFactorsJson")),
                ConfidenceThreshold = entity.GetInt32("ConfidenceThreshold") ?? 40,
                Explanation = entity.GetString("Explanation") ?? string.Empty,
                Remediation = DeserializeJson<List<RemediationStep>>(entity.GetString("RemediationJson")),
                RelatedDocs = DeserializeJson<List<RelatedDoc>>(entity.GetString("RelatedDocsJson")),
                Tags = DeserializeJsonArray(entity.GetString("TagsJson")),
                CreatedAt = entity.GetDateTimeOffset("CreatedAt")?.UtcDateTime ?? DateTime.UtcNow,
                UpdatedAt = entity.GetDateTimeOffset("UpdatedAt")?.UtcDateTime ?? DateTime.UtcNow
            };
        }

        // ===== IME LOG PATTERNS METHODS =====

        /// <summary>
        /// Stores or updates an IME log pattern
        /// PartitionKey: TenantId (or "global" for built-in), RowKey: PatternId
        /// </summary>
        public async Task<bool> StoreImeLogPatternAsync(ImeLogPattern pattern, string tenantId = "global")
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.ImeLogPatterns);

                var entity = new TableEntity(tenantId, pattern.PatternId)
                {
                    ["Category"] = pattern.Category ?? string.Empty,
                    ["Pattern"] = pattern.Pattern ?? string.Empty,
                    ["Action"] = pattern.Action ?? string.Empty,
                    ["ParametersJson"] = JsonConvert.SerializeObject(pattern.Parameters ?? new Dictionary<string, string>()),
                    ["Enabled"] = pattern.Enabled,
                    ["Description"] = pattern.Description ?? string.Empty,
                    ["IsBuiltIn"] = pattern.IsBuiltIn
                };

                await tableClient.UpsertEntityAsync(entity);
                _logger.LogDebug($"Stored IME log pattern {pattern.PatternId} for {tenantId}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to store IME log pattern {pattern.PatternId}");
                return false;
            }
        }

        /// <summary>
        /// Gets IME log patterns for a partition (tenant or "global")
        /// </summary>
        public async Task<List<ImeLogPattern>> GetImeLogPatternsAsync(string partitionKey)
        {
            if (partitionKey != "global")
                SecurityValidator.EnsureValidGuid(partitionKey, nameof(partitionKey));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.ImeLogPatterns);
                var query = tableClient.QueryAsync<TableEntity>(filter: $"PartitionKey eq '{partitionKey}'");

                var patterns = new List<ImeLogPattern>();
                await foreach (var entity in query)
                {
                    patterns.Add(MapToImeLogPattern(entity));
                }

                return patterns;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get IME log patterns for {partitionKey}");
                return new List<ImeLogPattern>();
            }
        }

        /// <summary>
        /// Deletes an IME log pattern
        /// </summary>
        public async Task<bool> DeleteImeLogPatternAsync(string tenantId, string patternId)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.ImeLogPatterns);
                await tableClient.DeleteEntityAsync(tenantId, patternId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to delete IME log pattern {patternId}");
                return false;
            }
        }

        private ImeLogPattern MapToImeLogPattern(TableEntity entity)
        {
            return new ImeLogPattern
            {
                PatternId = entity.RowKey,
                Category = entity.GetString("Category") ?? string.Empty,
                Pattern = entity.GetString("Pattern") ?? string.Empty,
                Action = entity.GetString("Action") ?? string.Empty,
                Parameters = DeserializeJson<Dictionary<string, string>>(entity.GetString("ParametersJson")),
                Enabled = entity.GetBoolean("Enabled") ?? true,
                Description = entity.GetString("Description") ?? string.Empty,
                IsBuiltIn = entity.GetBoolean("IsBuiltIn") ?? false
            };
        }
    }
}
