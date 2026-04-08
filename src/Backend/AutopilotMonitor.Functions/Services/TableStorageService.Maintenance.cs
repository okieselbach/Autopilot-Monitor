using Azure.Data.Tables;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
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

        /// <summary>
        /// Gets audit log entries across all tenants (Global Admin Mode)
        /// </summary>
        public async Task<List<AuditLogEntry>> GetAllAuditLogsAsync(int maxResults = 100)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.AuditLogs);
                var query = tableClient.QueryAsync<TableEntity>();

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
                _logger.LogError(ex, "Failed to get all audit logs");
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
        /// Gets sessions where the device has been actively sending data for longer than
        /// <paramref name="maxSessionWindowHours"/>.
        /// Status-independent: detects excessive data senders regardless of session status.
        /// Uses LastEventAt (written on every event batch) for the "still active" check.
        /// Sessions without LastEventAt (predating this field) are not returned.
        ///
        /// The OData pre-filter narrows candidates to sessions that straddle the cutoff boundary,
        /// then a post-filter verifies the actual session duration (LastEventAt − StartedAt)
        /// exceeds the allowed window. This prevents false positives from short sessions that
        /// merely happen to straddle the cutoff time.
        /// </summary>
        public async Task<List<SessionSummary>> GetExcessiveDataSendersAsync(string tenantId, DateTime windowCutoff, int maxSessionWindowHours)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);

                // OData pre-filter: narrow to sessions that straddle the cutoff boundary.
                // Status eq 'InProgress' → only block devices whose session is still actively
                //   sending data. Completed sessions (Succeeded/Failed) must NOT be re-blocked
                //   even if their LastEventAt is still within the window — they cannot continue
                //   to abuse data transfer. This also defends against ghost-blocks caused by
                //   devices with bad clocks: an agent that submits events with timestamps from
                //   weeks in the past pushes StartedAt back, making the session look long-lived;
                //   but once the session is Succeeded/Failed, no further data flows from it.
                // IsPreProvisioned ne true → exclude WhiteGlove sessions: a pre-provisioned device
                //   that resumes after weeks in storage looks like an excessive sender (StartedAt old,
                //   LastEventAt recent) but is a legitimate resumption, not abuse.
                var cutoffStr = windowCutoff.ToString("yyyy-MM-ddTHH:mm:ss");
                var filter = $"PartitionKey eq '{tenantId}' " +
                             $"and Status eq 'InProgress' " +
                             $"and LastEventAt gt datetime'{cutoffStr}Z' " +
                             $"and StartedAt lt datetime'{cutoffStr}Z' " +
                             $"and IsPreProvisioned ne true";

                var query = tableClient.QueryAsync<TableEntity>(filter: filter);
                var maxDuration = TimeSpan.FromHours(maxSessionWindowHours);

                var sessions = new List<SessionSummary>();
                await foreach (var entity in query)
                {
                    // Post-filter: verify actual session duration exceeds the window.
                    // OData cannot compute date differences, so we check in code.
                    var startedAt = entity.GetDateTimeOffset("StartedAt")?.UtcDateTime;
                    var lastEventAt = entity.GetDateTimeOffset("LastEventAt")?.UtcDateTime;

                    if (startedAt.HasValue && lastEventAt.HasValue
                        && (lastEventAt.Value - startedAt.Value) < maxDuration)
                    {
                        continue;
                    }

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

        // ===== SESSION INDEX BACKFILL =====

        /// <summary>
        /// Backfills the SessionsIndex table from the Sessions table.
        /// Finds sessions that don't have an IndexRowKey property and creates the corresponding
        /// index entry. Idempotent — safe to run repeatedly.
        /// Returns the number of sessions backfilled.
        /// </summary>
        public async Task<int> BackfillSessionIndexAsync()
        {
            try
            {
                var sessionsTable = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);
                var query = sessionsTable.QueryAsync<TableEntity>();

                int backfilledCount = 0;

                await foreach (var entity in query)
                {
                    var existingIndexRowKey = entity.GetString("IndexRowKey");
                    if (!string.IsNullOrEmpty(existingIndexRowKey))
                        continue; // Already indexed

                    var startedAt = entity.GetDateTimeOffset("StartedAt")?.UtcDateTime ?? DateTime.UtcNow;

                    try
                    {
                        await UpsertSessionIndexAsync(entity, startedAt);
                        backfilledCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to backfill session index for {TenantId}/{SessionId}",
                            entity.PartitionKey, entity.RowKey);
                    }
                }

                if (backfilledCount > 0)
                {
                    _logger.LogInformation("Session index backfill completed: {Count} sessions indexed", backfilledCount);
                }

                return backfilledCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session index backfill failed");
                return 0;
            }
        }

        /// <summary>
        /// One-time cleanup: removes ghost entries from SessionsIndex where a SessionId has
        /// multiple index rows (caused by a bug where StoreSessionAsync's Replace mode deleted
        /// IndexRowKey, preventing old index entries from being cleaned up when StartedAt shifted).
        /// Fixed in the same release — this cleanup can be removed once all environments have run it.
        /// TODO: Remove after 2026-06-01 (3 months grace period).
        /// </summary>
        public async Task<int> CleanupGhostSessionIndexEntriesAsync()
        {
            try
            {
                var indexTable = _tableServiceClient.GetTableClient(Constants.TableNames.SessionsIndex);
                var sessionsTable = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);

                // Build a map of SessionId → list of index RowKeys (across all tenants)
                var sessionIndexEntries = new Dictionary<string, List<(string PartitionKey, string RowKey, int EventCount)>>();

                var query = indexTable.QueryAsync<TableEntity>(
                    select: new[] { "PartitionKey", "RowKey", "SessionId", "EventCount" });

                await foreach (var entity in query)
                {
                    var sessionId = entity.GetString("SessionId") ?? ExtractSessionIdFromIndexRowKey(entity.RowKey);
                    var key = $"{entity.PartitionKey}_{sessionId}";
                    var eventCount = entity.GetInt32("EventCount") ?? 0;

                    if (!sessionIndexEntries.ContainsKey(key))
                        sessionIndexEntries[key] = new List<(string, string, int)>();

                    sessionIndexEntries[key].Add((entity.PartitionKey, entity.RowKey, eventCount));
                }

                int deletedCount = 0;

                foreach (var (key, entries) in sessionIndexEntries)
                {
                    if (entries.Count <= 1)
                        continue; // No duplicates

                    // Keep the entry with the highest EventCount (most up-to-date);
                    // delete all others as ghosts.
                    var sorted = entries.OrderByDescending(e => e.EventCount).ToList();
                    var keep = sorted[0];

                    for (int i = 1; i < sorted.Count; i++)
                    {
                        var ghost = sorted[i];
                        try
                        {
                            await indexTable.DeleteEntityAsync(ghost.PartitionKey, ghost.RowKey);
                            deletedCount++;
                            _logger.LogInformation(
                                "Deleted ghost SessionsIndex entry: {PartitionKey}/{RowKey} (EventCount={GhostCount}, kept entry has EventCount={KeptCount})",
                                ghost.PartitionKey, ghost.RowKey, ghost.EventCount, keep.EventCount);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to delete ghost index entry {PartitionKey}/{RowKey}",
                                ghost.PartitionKey, ghost.RowKey);
                        }
                    }
                }

                if (deletedCount > 0)
                {
                    _logger.LogInformation("Ghost SessionsIndex cleanup completed: {Count} ghost entries removed", deletedCount);
                }

                return deletedCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ghost SessionsIndex cleanup failed");
                return 0;
            }
        }

        /// <summary>
        /// Checks if the SessionsIndex table is empty.
        /// Used by startup backfill to determine if a full migration is needed.
        /// </summary>
        public async Task<bool> IsSessionIndexEmptyAsync()
        {
            try
            {
                var indexTable = _tableServiceClient.GetTableClient(Constants.TableNames.SessionsIndex);
                var query = indexTable.QueryAsync<TableEntity>(maxPerPage: 1, select: new[] { "PartitionKey" });

                await foreach (var _ in query)
                {
                    return false; // At least one entity exists
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check if session index is empty");
                return true; // Assume empty on error → trigger backfill
            }
        }
    }
}
