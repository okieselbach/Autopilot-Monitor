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
        // ===== AUDIT LOG METHODS =====

        /// <summary>
        /// Logs an audit entry
        /// </summary>
        public async Task<bool> LogAuditEntryAsync(string tenantId, string action, string entityType, string entityId, string performedBy, Dictionary<string, string>? details = null)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.AuditLogs);
                var timestamp = DateTime.UtcNow;

                var entity = new TableEntity(tenantId, Guid.NewGuid().ToString())
                {
                    { "Action", action },
                    { "EntityType", entityType },
                    { "EntityId", entityId },
                    { "PerformedBy", performedBy },
                    { "Timestamp", timestamp },
                    { "Details", details != null ? JsonConvert.SerializeObject(details) : string.Empty }
                };

                await tableClient.AddEntityAsync(entity);
                _logger.LogInformation($"Audit log created: {action} on {entityType} {entityId} by {performedBy}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create audit log entry");
                return false;
            }
        }

        /// <summary>
        /// Gets audit log entries for a tenant
        /// </summary>
        public async Task<List<AuditLogEntry>> GetAuditLogsAsync(string tenantId, int maxResults = 100)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.AuditLogs);
                var query = tableClient.QueryAsync<TableEntity>(filter: $"PartitionKey eq '{tenantId}'");

                var logs = new List<AuditLogEntry>();
                await foreach (var entity in query)
                {
                    logs.Add(new AuditLogEntry
                    {
                        Id = entity.RowKey,
                        TenantId = entity.PartitionKey,
                        Action = entity.GetString("Action") ?? string.Empty,
                        EntityType = entity.GetString("EntityType") ?? string.Empty,
                        EntityId = entity.GetString("EntityId") ?? string.Empty,
                        PerformedBy = entity.GetString("PerformedBy") ?? string.Empty,
                        Timestamp = entity.GetDateTimeOffset("Timestamp")?.UtcDateTime ?? DateTime.UtcNow,
                        Details = entity.GetString("Details") ?? string.Empty
                    });

                    if (logs.Count >= maxResults) break;
                }

                // Sort by timestamp descending (most recent first)
                return logs.OrderByDescending(l => l.Timestamp).Take(maxResults).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get audit logs for tenant {tenantId}");
                return new List<AuditLogEntry>();
            }
        }

        // ===== DATA RETENTION METHODS =====

        /// <summary>
        /// Gets sessions older than a specific date for a tenant
        /// </summary>
        public async Task<List<SessionSummary>> GetSessionsOlderThanAsync(string tenantId, DateTime cutoffDate)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);

                // Query sessions for this tenant older than cutoff date
                var filter = $"PartitionKey eq '{tenantId}' and StartedAt lt datetime'{cutoffDate:yyyy-MM-ddTHH:mm:ss}Z'";
                var query = tableClient.QueryAsync<TableEntity>(filter: filter);

                var sessions = new List<SessionSummary>();
                await foreach (var entity in query)
                {
                    sessions.Add(MapToSessionSummary(entity));
                }

                _logger.LogInformation($"Found {sessions.Count} sessions older than {cutoffDate:yyyy-MM-dd} for tenant {tenantId}");
                return sessions;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get old sessions for tenant {tenantId}");
                return new List<SessionSummary>();
            }
        }

        /// <summary>
        /// Gets all sessions within a date range, optionally filtered by tenant.
        /// Uses server-side filtering to avoid loading all sessions into memory.
        /// </summary>
        public async Task<List<SessionSummary>> GetSessionsByDateRangeAsync(DateTime startDate, DateTime endDate, string? tenantId = null)
        {
            if (!string.IsNullOrEmpty(tenantId))
                SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);

                var filter = !string.IsNullOrEmpty(tenantId)
                    ? $"PartitionKey eq '{tenantId}' and StartedAt ge datetime'{startDate:yyyy-MM-ddTHH:mm:ss}Z' and StartedAt lt datetime'{endDate:yyyy-MM-ddTHH:mm:ss}Z'"
                    : $"StartedAt ge datetime'{startDate:yyyy-MM-ddTHH:mm:ss}Z' and StartedAt lt datetime'{endDate:yyyy-MM-ddTHH:mm:ss}Z'";

                var query = tableClient.QueryAsync<TableEntity>(filter: filter);

                var sessions = new List<SessionSummary>();
                await foreach (var entity in query)
                {
                    sessions.Add(MapToSessionSummary(entity));
                }

                _logger.LogInformation($"Found {sessions.Count} sessions between {startDate:yyyy-MM-dd} and {endDate:yyyy-MM-dd}" +
                    (tenantId != null ? $" for tenant {tenantId}" : ""));
                return sessions;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get sessions by date range");
                return new List<SessionSummary>();
            }
        }

        /// <summary>
        /// Gets stalled sessions (InProgress status, started before cutoff time) for a tenant.
        /// Uses server-side filtering to avoid loading all sessions into memory.
        /// </summary>
        public async Task<List<SessionSummary>> GetStalledSessionsAsync(string tenantId, DateTime cutoffTime)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);

                var filter = $"PartitionKey eq '{tenantId}' and Status eq 'InProgress' and StartedAt lt datetime'{cutoffTime:yyyy-MM-ddTHH:mm:ss}Z'";
                var query = tableClient.QueryAsync<TableEntity>(filter: filter);

                var sessions = new List<SessionSummary>();
                await foreach (var entity in query)
                {
                    sessions.Add(MapToSessionSummary(entity));
                }

                return sessions;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get stalled sessions for tenant {tenantId}");
                return new List<SessionSummary>();
            }
        }

        /// <summary>
        /// Gets sessions where the device has sent data within the last window period
        /// AND the session started before that window cutoff – indicating a device that has been
        /// actively sending data for longer than the allowed maximum.
        /// Status-independent: detects excessive data senders regardless of session status.
        /// Uses LastEventAt (written on every event batch) for the "still active" check.
        /// Sessions without LastEventAt (predating this field) are not returned.
        /// </summary>
        public async Task<List<SessionSummary>> GetExcessiveDataSendersAsync(string tenantId, DateTime windowCutoff)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);

                // LastEventAt gt cutoff  → device sent data recently (still active)
                // StartedAt lt cutoff    → session has been running longer than the allowed window
                // IsPreProvisioned ne true → exclude WhiteGlove sessions: a pre-provisioned device
                //   that resumes after weeks in storage looks like an excessive sender (StartedAt old,
                //   LastEventAt recent) but is a legitimate resumption, not abuse.
                var cutoffStr = windowCutoff.ToString("yyyy-MM-ddTHH:mm:ss");
                var filter = $"PartitionKey eq '{tenantId}' " +
                             $"and LastEventAt gt datetime'{cutoffStr}Z' " +
                             $"and StartedAt lt datetime'{cutoffStr}Z' " +
                             $"and IsPreProvisioned ne true";

                var query = tableClient.QueryAsync<TableEntity>(filter: filter);

                var sessions = new List<SessionSummary>();
                await foreach (var entity in query)
                {
                    sessions.Add(MapToSessionSummary(entity));
                }

                return sessions;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get excessive data sender sessions for tenant {tenantId}");
                return new List<SessionSummary>();
            }
        }

        /// <summary>
        /// Gets all unique tenant IDs from sessions table
        /// </summary>
        public async Task<List<string>> GetAllTenantIdsAsync()
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);
                var query = tableClient.QueryAsync<TableEntity>(select: new[] { "PartitionKey" });

                var tenantIds = new HashSet<string>();
                await foreach (var entity in query)
                {
                    tenantIds.Add(entity.PartitionKey);
                }

                _logger.LogInformation($"Found {tenantIds.Count} unique tenants");
                return tenantIds.ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get tenant IDs");
                return new List<string>();
            }
        }

        // ===== DELETION HELPERS =====

        /// <summary>
        /// Deletes all events for a session from storage
        /// </summary>
        public async Task<int> DeleteSessionEventsAsync(string tenantId, string sessionId)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Events);

                // Query all events for this session using the same PartitionKey format as StoreEventAsync
                var partitionKey = $"{tenantId}_{sessionId}";
                var filter = $"PartitionKey eq '{partitionKey}'";
                var events = tableClient.QueryAsync<TableEntity>(filter);

                int deletedCount = 0;
                var batch = new List<TableTransactionAction>();
                await foreach (var eventEntity in events)
                {
                    batch.Add(new TableTransactionAction(TableTransactionActionType.Delete, eventEntity));
                    deletedCount++;
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

                _logger.LogInformation($"Deleted {deletedCount} events for session {sessionId}");
                return deletedCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to delete events for session {sessionId}");
                return 0;
            }
        }

        /// <summary>
        /// Deletes all rule results for a session
        /// </summary>
        public async Task<int> DeleteSessionRuleResultsAsync(string tenantId, string sessionId)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.RuleResults);
                var partitionKey = $"{tenantId}_{sessionId}";
                var query = tableClient.QueryAsync<TableEntity>(filter: $"PartitionKey eq '{partitionKey}'");

                int deletedCount = 0;
                var batch = new List<TableTransactionAction>();
                await foreach (var entity in query)
                {
                    batch.Add(new TableTransactionAction(TableTransactionActionType.Delete, entity));
                    deletedCount++;
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

                return deletedCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to delete rule results for session {sessionId}");
                return 0;
            }
        }

        /// <summary>
        /// Deletes all app install summaries for a session
        /// </summary>
        public async Task<int> DeleteSessionAppInstallSummariesAsync(string tenantId, string sessionId)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.AppInstallSummaries);
                var query = tableClient.QueryAsync<TableEntity>(
                    filter: $"PartitionKey eq '{tenantId}' and SessionId eq '{sessionId}'");

                int deletedCount = 0;
                var batch = new List<TableTransactionAction>();
                await foreach (var entity in query)
                {
                    batch.Add(new TableTransactionAction(TableTransactionActionType.Delete, entity));
                    deletedCount++;
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

                return deletedCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to delete app install summaries for session {sessionId}");
                return 0;
            }
        }
    }
}
