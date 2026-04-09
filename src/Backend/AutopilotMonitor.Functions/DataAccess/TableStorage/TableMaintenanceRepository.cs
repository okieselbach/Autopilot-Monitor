using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Functions.Services;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Table Storage implementation of IMaintenanceRepository.
    /// Delegates to existing TableStorageService for backwards compatibility.
    /// </summary>
    public class TableMaintenanceRepository : IMaintenanceRepository
    {
        private readonly TableStorageService _storage;
        private readonly ILogger<TableMaintenanceRepository> _logger;

        public TableMaintenanceRepository(TableStorageService storage, ILogger<TableMaintenanceRepository> logger)
        {
            _storage = storage;
            _logger = logger;
        }

        public Task<bool> LogAuditEntryAsync(string tenantId, string action, string entityType,
            string entityId, string performedBy, Dictionary<string, string>? details = null)
            => _storage.LogAuditEntryAsync(tenantId, action, entityType, entityId, performedBy, details);

        public Task<List<AuditLogEntry>> GetAuditLogsAsync(string tenantId, int maxResults = 100)
            => _storage.GetAuditLogsAsync(tenantId, maxResults);

        public Task<List<AuditLogEntry>> GetAllAuditLogsAsync(int maxResults = 100)
            => _storage.GetAllAuditLogsAsync(maxResults);

        public Task<List<SessionSummary>> GetSessionsOlderThanAsync(string tenantId, DateTime cutoffDate)
            => _storage.GetSessionsOlderThanAsync(tenantId, cutoffDate);

        public Task<List<SessionSummary>> GetSessionsByDateRangeAsync(DateTime startDate, DateTime endDate, string? tenantId = null)
            => _storage.GetSessionsByDateRangeAsync(startDate, endDate, tenantId);

        public Task<List<SessionSummary>> GetStalledSessionsAsync(string tenantId, DateTime cutoffTime)
            => _storage.GetStalledSessionsAsync(tenantId, cutoffTime);

        public Task<List<SessionSummary>> GetAgentSilentSessionsAsync(string tenantId, DateTime silenceCutoff, DateTime hardCutoff)
            => _storage.GetAgentSilentSessionsAsync(tenantId, silenceCutoff, hardCutoff);

        public Task<List<SessionSummary>> GetExcessiveDataSendersAsync(string tenantId, DateTime windowCutoff, int maxSessionWindowHours)
            => _storage.GetExcessiveDataSendersAsync(tenantId, windowCutoff, maxSessionWindowHours);

        public Task<List<string>> GetAllTenantIdsAsync()
            => _storage.GetAllTenantIdsAsync();

        public Task<int> DeleteSessionEventsAsync(string tenantId, string sessionId)
            => _storage.DeleteSessionEventsAsync(tenantId, sessionId);

        public Task<int> DeleteSessionRuleResultsAsync(string tenantId, string sessionId)
            => _storage.DeleteSessionRuleResultsAsync(tenantId, sessionId);

        public Task<int> DeleteSessionAppInstallSummariesAsync(string tenantId, string sessionId)
            => _storage.DeleteSessionAppInstallSummariesAsync(tenantId, sessionId);

        public Task<int> BackfillSessionIndexAsync()
            => _storage.BackfillSessionIndexAsync();

        public Task<int> CleanupGhostSessionIndexEntriesAsync()
            => _storage.CleanupGhostSessionIndexEntriesAsync();

        public Task<bool> IsSessionIndexEmptyAsync()
            => _storage.IsSessionIndexEmptyAsync();

        // --- Tenant Offboarding ---

        public async Task<Dictionary<string, int>> DeleteAllTenantDataAsync(string tenantId)
        {
            var deletedCounts = new Dictionary<string, int>();
            var normalizedTenantId = tenantId.ToLowerInvariant();

            // Tables where PartitionKey = tenantId (normalized lowercase)
            var tenantPartitionTables = new[]
            {
                Constants.TableNames.Sessions,
                Constants.TableNames.SessionsIndex,
                Constants.TableNames.AuditLogs,
                Constants.TableNames.UsageMetrics,
                Constants.TableNames.UserActivity,
                Constants.TableNames.GatherRules,
                Constants.TableNames.AnalyzeRules,
                Constants.TableNames.AppInstallSummaries,
                Constants.TableNames.TenantConfiguration,
                Constants.TableNames.TenantAdmins,
                Constants.TableNames.BootstrapSessions,
                Constants.TableNames.BlockedDevices,
                Constants.TableNames.ImeLogPatterns,
                Constants.TableNames.RuleStates,
            };

            foreach (var tableName in tenantPartitionTables)
            {
                var deleted = await DeleteAllRowsByPartitionKeyAsync(tableName, normalizedTenantId);
                deletedCounts[tableName] = deleted;
                _logger.LogInformation("Offboard [{TenantId}] {TableName}: deleted {Deleted} rows", tenantId, tableName, deleted);
            }

            // Tables with composite PartitionKey = "{tenantId}_{sessionId}" – query by prefix
            var eventsDeleted = await DeleteByTenantPrefixAsync(Constants.TableNames.Events, normalizedTenantId);
            deletedCounts[Constants.TableNames.Events] = eventsDeleted;
            _logger.LogInformation("Offboard [{TenantId}] Events: deleted {Deleted} rows", tenantId, eventsDeleted);

            var ruleResultsDeleted = await DeleteByTenantPrefixAsync(Constants.TableNames.RuleResults, normalizedTenantId);
            deletedCounts[Constants.TableNames.RuleResults] = ruleResultsDeleted;
            _logger.LogInformation("Offboard [{TenantId}] RuleResults: deleted {Deleted} rows", tenantId, ruleResultsDeleted);

            // BootstrapSessions CodeLookup entries (PartitionKey = "CodeLookup", TenantId property = tenantId)
            var codeLookupDeleted = await DeleteCodeLookupEntriesAsync(normalizedTenantId);
            deletedCounts["BootstrapSessions_CodeLookup"] = codeLookupDeleted;
            _logger.LogInformation("Offboard [{TenantId}] BootstrapSessions CodeLookup: deleted {Deleted} rows", tenantId, codeLookupDeleted);

            return deletedCounts;
        }

        /// <summary>
        /// Deletes all rows in a table where PartitionKey equals the given value.
        /// Uses batched deletes (max 100 per transaction, same PartitionKey required).
        /// </summary>
        private async Task<int> DeleteAllRowsByPartitionKeyAsync(string tableName, string partitionKey)
        {
            try
            {
                var tableClient = _storage.GetTableClient(tableName);
                var filter = $"PartitionKey eq '{partitionKey}'";
                var entities = tableClient.QueryAsync<TableEntity>(filter, select: new[] { "PartitionKey", "RowKey" });

                int deleted = 0;
                var batch = new List<TableTransactionAction>();

                await foreach (var entity in entities)
                {
                    batch.Add(new TableTransactionAction(TableTransactionActionType.Delete, entity));
                    deleted++;

                    if (batch.Count >= 100)
                    {
                        await tableClient.SubmitTransactionAsync(batch);
                        batch.Clear();
                    }
                }

                if (batch.Count > 0)
                {
                    await tableClient.SubmitTransactionAsync(batch);
                }

                return deleted;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete rows in {TableName} for partition {PartitionKey}", tableName, partitionKey);
                return 0;
            }
        }

        /// <summary>
        /// Deletes all rows for a tenant from a table that uses composite PartitionKey = "{tenantId}_{sessionId}".
        /// Used for Events and RuleResults tables.
        /// </summary>
        private async Task<int> DeleteByTenantPrefixAsync(string tableName, string normalizedTenantId)
        {
            try
            {
                var tableClient = _storage.GetTableClient(tableName);
                // OData startsWith: PartitionKey ge 'prefix' and PartitionKey lt 'prefix~'
                var prefix = normalizedTenantId + "_";
                var filter = $"PartitionKey ge '{prefix}' and PartitionKey lt '{prefix}~'";
                var entities = tableClient.QueryAsync<TableEntity>(filter, select: new[] { "PartitionKey", "RowKey" });

                int deleted = 0;
                // Group by PartitionKey because Azure Table batch transactions require same PartitionKey
                var groups = new Dictionary<string, List<TableEntity>>();

                await foreach (var entity in entities)
                {
                    if (!groups.ContainsKey(entity.PartitionKey))
                        groups[entity.PartitionKey] = new List<TableEntity>();
                    groups[entity.PartitionKey].Add(entity);
                    deleted++;
                }

                foreach (var group in groups.Values)
                {
                    for (int i = 0; i < group.Count; i += 100)
                    {
                        var chunk = group.Skip(i).Take(100).ToList();
                        var batch = chunk.Select(e => new TableTransactionAction(TableTransactionActionType.Delete, e)).ToList();
                        await tableClient.SubmitTransactionAsync(batch);
                    }
                }

                return deleted;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete {TableName} for tenant {TenantId}", tableName, normalizedTenantId);
                return 0;
            }
        }

        /// <summary>
        /// Deletes BootstrapSessions CodeLookup entries for a tenant.
        /// These use PartitionKey = "CodeLookup" with a TenantId property matching the tenant.
        /// </summary>
        private async Task<int> DeleteCodeLookupEntriesAsync(string normalizedTenantId)
        {
            try
            {
                var tableClient = _storage.GetTableClient(Constants.TableNames.BootstrapSessions);
                var filter = $"PartitionKey eq 'CodeLookup' and TenantId eq '{normalizedTenantId}'";
                var entities = tableClient.QueryAsync<TableEntity>(filter, select: new[] { "PartitionKey", "RowKey" });

                int deleted = 0;
                var batch = new List<TableTransactionAction>();

                await foreach (var entity in entities)
                {
                    batch.Add(new TableTransactionAction(TableTransactionActionType.Delete, entity));
                    deleted++;

                    if (batch.Count >= 100)
                    {
                        await tableClient.SubmitTransactionAsync(batch);
                        batch.Clear();
                    }
                }

                if (batch.Count > 0)
                {
                    await tableClient.SubmitTransactionAsync(batch);
                }

                return deleted;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete CodeLookup entries for tenant {TenantId}", normalizedTenantId);
                return 0;
            }
        }
    }
}
