using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Service for managing Azure Table Storage operations
    /// </summary>
    public class TableStorageService
    {
        private readonly TableServiceClient _tableServiceClient;
        private readonly ILogger<TableStorageService> _logger;
        private const string SessionsTableName = "Sessions";
        private const string EventsTableName = "Events";
        private const string AuditLogsTableName = "AuditLogs";
        private const string UsageMetricsTableName = "UsageMetrics";
        private const string RuleResultsTableName = "RuleResults";
        private const string GatherRulesTableName = "GatherRules";
        private const string AnalyzeRulesTableName = "AnalyzeRules";
        private const string UserActivityTableName = "UserActivity";

        public TableStorageService(IConfiguration configuration, ILogger<TableStorageService> logger)
        {
            _logger = logger;
            var connectionString = configuration["AzureTableStorageConnectionString"];
            _tableServiceClient = new TableServiceClient(connectionString);

            // Ensure tables exist
            InitializeTablesAsync().GetAwaiter().GetResult();
        }

        private async Task InitializeTablesAsync()
        {
            try
            {
                await _tableServiceClient.CreateTableIfNotExistsAsync(SessionsTableName);
                await _tableServiceClient.CreateTableIfNotExistsAsync(EventsTableName);
                await _tableServiceClient.CreateTableIfNotExistsAsync(AuditLogsTableName);
                await _tableServiceClient.CreateTableIfNotExistsAsync(UsageMetricsTableName);
                await _tableServiceClient.CreateTableIfNotExistsAsync(RuleResultsTableName);
                await _tableServiceClient.CreateTableIfNotExistsAsync(GatherRulesTableName);
                await _tableServiceClient.CreateTableIfNotExistsAsync(AnalyzeRulesTableName);
                await _tableServiceClient.CreateTableIfNotExistsAsync(UserActivityTableName);
                _logger.LogInformation("Tables initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize tables");
            }
        }

        /// <summary>
        /// Stores a session registration
        /// </summary>
        public async Task<bool> StoreSessionAsync(SessionRegistration registration)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(SessionsTableName);

                var entity = new TableEntity(registration.TenantId, registration.SessionId)
                {
                    ["SerialNumber"] = registration.SerialNumber ?? string.Empty,
                    ["Manufacturer"] = registration.Manufacturer ?? string.Empty,
                    ["Model"] = registration.Model ?? string.Empty,
                    ["DeviceName"] = registration.DeviceName ?? string.Empty,
                    ["OsBuild"] = registration.OsBuild ?? string.Empty,
                    ["OsEdition"] = registration.OsEdition ?? string.Empty,
                    ["OsLanguage"] = registration.OsLanguage ?? string.Empty,
                    ["AutopilotProfileName"] = registration.AutopilotProfileName ?? string.Empty,
                    ["AutopilotProfileId"] = registration.AutopilotProfileId ?? string.Empty,
                    ["IsUserDriven"] = registration.IsUserDriven,
                    ["IsPreProvisioned"] = registration.IsPreProvisioned,
                    ["StartedAt"] = registration.StartedAt,
                    ["AgentVersion"] = registration.AgentVersion ?? string.Empty,
                    ["CurrentPhase"] = (int)EnrollmentPhase.PreFlight,
                    ["Status"] = SessionStatus.InProgress.ToString(),
                    ["EventCount"] = 0
                };

                await tableClient.UpsertEntityAsync(entity);
                _logger.LogInformation($"Stored session {registration.SessionId}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to store session {registration.SessionId}");
                return false;
            }
        }

        /// <summary>
        /// Stores an event
        /// </summary>
        public async Task<bool> StoreEventAsync(EnrollmentEvent evt)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(EventsTableName);

                // PartitionKey: TenantId_SessionId for efficient querying
                // RowKey: Timestamp_Sequence for ordering
                var partitionKey = $"{evt.TenantId}_{evt.SessionId}";
                var rowKey = $"{evt.Timestamp:yyyyMMddHHmmssfff}_{evt.Sequence:D10}";

                var entity = new TableEntity(partitionKey, rowKey)
                {
                    ["EventId"] = evt.EventId,
                    ["SessionId"] = evt.SessionId,
                    ["TenantId"] = evt.TenantId,
                    ["Timestamp"] = evt.Timestamp,
                    ["EventType"] = evt.EventType ?? string.Empty,
                    ["Severity"] = (int)evt.Severity,
                    ["Source"] = evt.Source ?? string.Empty,
                    ["Phase"] = (int)evt.Phase,
                    ["Message"] = evt.Message ?? string.Empty,
                    ["Sequence"] = evt.Sequence,
                    ["DataJson"] = evt.Data != null && evt.Data.Count > 0
                        ? JsonConvert.SerializeObject(evt.Data)
                        : string.Empty
                };

                await tableClient.UpsertEntityAsync(entity);
                _logger.LogDebug($"Stored event {evt.EventId}");

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to store event {evt.EventId}");
                return false;
            }
        }


        /// <summary>
        /// Gets all sessions for a tenant
        /// </summary>
        public async Task<List<SessionSummary>> GetSessionsAsync(string tenantId, int maxResults = 100)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(SessionsTableName);
                var sessions = new List<SessionSummary>();

                // Query sessions by tenant (PartitionKey)
                var query = tableClient.QueryAsync<TableEntity>(
                    filter: $"PartitionKey eq '{tenantId}'",
                    maxPerPage: maxResults
                );

                await foreach (var entity in query)
                {
                    sessions.Add(MapToSessionSummary(entity));
                }

                // Sort by StartedAt descending (most recent first)
                return sessions.OrderByDescending(s => s.StartedAt).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get sessions for tenant {tenantId}");
                return new List<SessionSummary>();
            }
        }

        /// <summary>
        /// Gets all sessions across all tenants (for galactic admin mode)
        /// </summary>
        public async Task<List<SessionSummary>> GetAllSessionsAsync(int maxResults = 100)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(SessionsTableName);
                var sessions = new List<SessionSummary>();

                // Query all sessions without tenant filter
                var query = tableClient.QueryAsync<TableEntity>(maxPerPage: maxResults);

                await foreach (var entity in query)
                {
                    sessions.Add(MapToSessionSummary(entity));
                    if (sessions.Count >= maxResults) break;
                }

                // Sort by StartedAt descending (most recent first)
                return sessions.OrderByDescending(s => s.StartedAt).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get all sessions");
                return new List<SessionSummary>();
            }
        }

        /// <summary>
        /// Gets a specific session
        /// </summary>
        public async Task<SessionSummary?> GetSessionAsync(string tenantId, string sessionId)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(SessionsTableName);
                var entity = await tableClient.GetEntityAsync<TableEntity>(tenantId, sessionId);
                return MapToSessionSummary(entity.Value);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get session {sessionId}");
                return null;
            }
        }

        /// <summary>
        /// Updates the session status and current phase
        /// </summary>
        public async Task<bool> UpdateSessionStatusAsync(string tenantId, string sessionId, SessionStatus status, EnrollmentPhase? currentPhase = null, string? failureReason = null)
        {
            const int maxRetries = 3;
            int retryCount = 0;

            while (retryCount < maxRetries)
            {
                try
                {
                    var tableClient = _tableServiceClient.GetTableClient(SessionsTableName);

                    // Get the existing entity
                    var entity = await tableClient.GetEntityAsync<TableEntity>(tenantId, sessionId);
                    var session = entity.Value;

                    // Update status
                    session["Status"] = status.ToString();

                    // Update current phase if provided
                    if (currentPhase.HasValue)
                    {
                        session["CurrentPhase"] = (int)currentPhase.Value;
                    }

                    // Set completion time if succeeded or failed
                    if (status == SessionStatus.Succeeded || status == SessionStatus.Failed)
                    {
                        session["CompletedAt"] = DateTime.UtcNow;
                    }

                    // Set failure reason if failed
                    if (status == SessionStatus.Failed && !string.IsNullOrEmpty(failureReason))
                    {
                        session["FailureReason"] = failureReason;
                    }

                    // Update event count (calculated on-demand during phase changes)
                    var eventsTableClient = _tableServiceClient.GetTableClient(EventsTableName);
                    var partitionKey = $"{tenantId}_{sessionId}";
                    var eventCountFilter = $"PartitionKey eq '{partitionKey}'";
                    var eventCount = 0;
                    await foreach (var _ in eventsTableClient.QueryAsync<TableEntity>(eventCountFilter, select: new[] { "RowKey" }))
                    {
                        eventCount++;
                    }
                    session["EventCount"] = eventCount;

                    // Update entity with optimistic concurrency
                    await tableClient.UpdateEntityAsync(session, session.ETag, TableUpdateMode.Replace);

                    _logger.LogInformation($"Updated session {sessionId} status to {status}");
                    return true;
                }
                catch (Azure.RequestFailedException ex) when (ex.Status == 412) // Precondition Failed (ETag conflict)
                {
                    retryCount++;
                    if (retryCount >= maxRetries)
                    {
                        _logger.LogWarning($"Failed to update session {sessionId} status after {maxRetries} retries due to ETag conflicts");
                        return false;
                    }

                    // Exponential backoff: 50ms, 100ms, 200ms
                    await Task.Delay(50 * (int)Math.Pow(2, retryCount - 1));
                    _logger.LogDebug($"Retrying session {sessionId} update (attempt {retryCount}/{maxRetries})");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to update session {sessionId} status");
                    return false;
                }
            }

            return false;
        }

        /// <summary>
        /// Gets all events for a specific session
        /// </summary>
        public async Task<List<EnrollmentEvent>> GetSessionEventsAsync(string tenantId, string sessionId, int maxResults = 1000)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(EventsTableName);
                var events = new List<EnrollmentEvent>();

                // Events are stored with PartitionKey = "{TenantId}_{SessionId}"
                var partitionKey = $"{tenantId}_{sessionId}";

                var query = tableClient.QueryAsync<TableEntity>(
                    filter: $"PartitionKey eq '{partitionKey}'",
                    maxPerPage: maxResults
                );

                await foreach (var entity in query)
                {
                    events.Add(MapToEnrollmentEvent(entity));
                }

                // Sort by Timestamp ascending (chronological order)
                return events.OrderBy(e => e.Timestamp).ThenBy(e => e.Sequence).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get events for session {sessionId}");
                return new List<EnrollmentEvent>();
            }
        }

        /// <summary>
        /// Maps a TableEntity to EnrollmentEvent
        /// </summary>
        private EnrollmentEvent MapToEnrollmentEvent(TableEntity entity)
        {
            return new EnrollmentEvent
            {
                EventId = entity.GetString("EventId") ?? string.Empty,
                SessionId = entity.GetString("SessionId") ?? string.Empty,
                TenantId = entity.PartitionKey,
                Timestamp = entity.GetDateTime("Timestamp") ?? DateTime.UtcNow,
                EventType = entity.GetString("EventType") ?? string.Empty,
                Severity = (EventSeverity)(entity.GetInt32("Severity") ?? 0),
                Source = entity.GetString("Source") ?? string.Empty,
                Phase = (EnrollmentPhase)(entity.GetInt32("Phase") ?? 0),
                Message = entity.GetString("Message") ?? string.Empty,
                Sequence = entity.GetInt64("Sequence") ?? 0,
                Data = entity.ContainsKey("DataJson") && entity.GetString("DataJson") != null
                    ? JsonConvert.DeserializeObject<Dictionary<string, object>>(entity.GetString("DataJson"))
                    : new Dictionary<string, object>()
            };
        }

        /// <summary>
        /// Maps a TableEntity to SessionSummary
        /// </summary>
        private SessionSummary MapToSessionSummary(TableEntity entity)
        {
            var startedAt = entity.GetDateTime("StartedAt") ?? DateTime.UtcNow;
            var completedAt = entity.GetDateTime("CompletedAt");

            // Parse status with error handling and case-insensitivity
            var statusString = entity.GetString("Status") ?? "InProgress";
            if (!Enum.TryParse<SessionStatus>(statusString, ignoreCase: true, out var status))
            {
                _logger.LogWarning($"Failed to parse status '{statusString}' for session {entity.RowKey}, defaulting to Unknown");
                status = SessionStatus.Unknown;
            }

            return new SessionSummary
            {
                SessionId = entity.RowKey,
                TenantId = entity.PartitionKey,
                SerialNumber = entity.GetString("SerialNumber") ?? string.Empty,
                DeviceName = entity.GetString("DeviceName") ?? string.Empty,
                Manufacturer = entity.GetString("Manufacturer") ?? string.Empty,
                Model = entity.GetString("Model") ?? string.Empty,
                StartedAt = startedAt,
                CompletedAt = completedAt,
                CurrentPhase = entity.GetInt32("CurrentPhase") ?? 0, // Now int, not enum
                CurrentPhaseDetail = entity.GetString("CurrentPhaseDetail") ?? string.Empty,
                Status = status,
                FailureReason = entity.GetString("FailureReason") ?? string.Empty,
                EventCount = entity.GetInt32("EventCount") ?? 0,
                DurationSeconds = completedAt.HasValue
                    ? (int)(completedAt.Value - startedAt).TotalSeconds
                    : (int)(DateTime.UtcNow - startedAt).TotalSeconds
            };
        }

        /// <summary>
        /// Deletes a session from storage
        /// </summary>
        public async Task<bool> DeleteSessionAsync(string tenantId, string sessionId)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(SessionsTableName);
                var response = await tableClient.DeleteEntityAsync(tenantId, sessionId);
                _logger.LogInformation($"Deleted session {sessionId} for tenant {tenantId}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to delete session {sessionId}");
                return false;
            }
        }

        /// <summary>
        /// Deletes all events for a session from storage
        /// </summary>
        public async Task<int> DeleteSessionEventsAsync(string tenantId, string sessionId)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(EventsTableName);

                // Query all events for this session using the same PartitionKey format as StoreEventAsync
                var partitionKey = $"{tenantId}_{sessionId}";
                var filter = $"PartitionKey eq '{partitionKey}'";
                var events = tableClient.QueryAsync<TableEntity>(filter);

                int deletedCount = 0;
                await foreach (var eventEntity in events)
                {
                    await tableClient.DeleteEntityAsync(eventEntity.PartitionKey, eventEntity.RowKey);
                    deletedCount++;
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
        /// Logs an audit entry
        /// </summary>
        public async Task<bool> LogAuditEntryAsync(string tenantId, string action, string entityType, string entityId, string performedBy, Dictionary<string, string>? details = null)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(AuditLogsTableName);
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
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(AuditLogsTableName);
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

        // ===== HISTORICAL METRICS METHODS =====

        /// <summary>
        /// Saves a historical metrics snapshot
        /// </summary>
        public async Task<bool> SaveUsageMetricsSnapshotAsync(UsageMetricsSnapshot metrics)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(UsageMetricsTableName);

                var entity = new TableEntity(metrics.Date, metrics.TenantId)
                {
                    ["ComputedAt"] = metrics.ComputedAt,
                    ["ComputeDurationMs"] = metrics.ComputeDurationMs,
                    ["SessionsTotal"] = metrics.SessionsTotal,
                    ["SessionsSucceeded"] = metrics.SessionsSucceeded,
                    ["SessionsFailed"] = metrics.SessionsFailed,
                    ["SessionsInProgress"] = metrics.SessionsInProgress,
                    ["SessionsSuccessRate"] = metrics.SessionsSuccessRate,
                    ["AvgDurationMinutes"] = metrics.AvgDurationMinutes,
                    ["MedianDurationMinutes"] = metrics.MedianDurationMinutes,
                    ["P95DurationMinutes"] = metrics.P95DurationMinutes,
                    ["P99DurationMinutes"] = metrics.P99DurationMinutes,
                    ["UniqueTenants"] = metrics.UniqueTenants,
                    ["UniqueUsers"] = metrics.UniqueUsers,
                    ["LoginCount"] = metrics.LoginCount,
                    ["TopManufacturers"] = metrics.TopManufacturers,
                    ["TopModels"] = metrics.TopModels
                };

                await tableClient.UpsertEntityAsync(entity);
                _logger.LogInformation($"Saved historical metrics for {metrics.Date} / {metrics.TenantId}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to save historical metrics for {metrics.Date} / {metrics.TenantId}");
                return false;
            }
        }

        /// <summary>
        /// Gets historical metrics for a date range
        /// </summary>
        public async Task<List<UsageMetricsSnapshot>> GetUsageMetricsSnapshotAsync(string? tenantId = null, string? startDate = null, string? endDate = null, int maxResults = 100)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(UsageMetricsTableName);

                // Build filter
                var filters = new List<string>();

                if (!string.IsNullOrEmpty(startDate))
                    filters.Add($"PartitionKey ge '{startDate}'");

                if (!string.IsNullOrEmpty(endDate))
                    filters.Add($"PartitionKey le '{endDate}'");

                if (!string.IsNullOrEmpty(tenantId))
                    filters.Add($"RowKey eq '{tenantId}'");

                var filter = filters.Count > 0 ? string.Join(" and ", filters) : null;
                var query = tableClient.QueryAsync<TableEntity>(filter: filter);

                var results = new List<UsageMetricsSnapshot>();
                await foreach (var entity in query)
                {
                    results.Add(new UsageMetricsSnapshot
                    {
                        Date = entity.PartitionKey,
                        TenantId = entity.RowKey,
                        ComputedAt = entity.GetDateTimeOffset("ComputedAt")?.UtcDateTime ?? DateTime.UtcNow,
                        ComputeDurationMs = entity.GetInt32("ComputeDurationMs") ?? 0,
                        SessionsTotal = entity.GetInt32("SessionsTotal") ?? 0,
                        SessionsSucceeded = entity.GetInt32("SessionsSucceeded") ?? 0,
                        SessionsFailed = entity.GetInt32("SessionsFailed") ?? 0,
                        SessionsInProgress = entity.GetInt32("SessionsInProgress") ?? 0,
                        SessionsSuccessRate = entity.GetDouble("SessionsSuccessRate") ?? 0,
                        AvgDurationMinutes = entity.GetDouble("AvgDurationMinutes") ?? 0,
                        MedianDurationMinutes = entity.GetDouble("MedianDurationMinutes") ?? 0,
                        P95DurationMinutes = entity.GetDouble("P95DurationMinutes") ?? 0,
                        P99DurationMinutes = entity.GetDouble("P99DurationMinutes") ?? 0,
                        UniqueTenants = entity.GetInt32("UniqueTenants") ?? 0,
                        UniqueUsers = entity.GetInt32("UniqueUsers") ?? 0,
                        LoginCount = entity.GetInt32("LoginCount") ?? 0,
                        TopManufacturers = entity.GetString("TopManufacturers") ?? "[]",
                        TopModels = entity.GetString("TopModels") ?? "[]"
                    });

                    if (results.Count >= maxResults) break;
                }

                return results.OrderByDescending(m => m.Date).Take(maxResults).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get historical metrics");
                return new List<UsageMetricsSnapshot>();
            }
        }

        // ===== DATA RETENTION METHODS =====

        /// <summary>
        /// Gets sessions older than a specific date for a tenant
        /// </summary>
        public async Task<List<SessionSummary>> GetSessionsOlderThanAsync(string tenantId, DateTime cutoffDate)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(SessionsTableName);

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
        /// Gets all unique tenant IDs from sessions table
        /// </summary>
        public async Task<List<string>> GetAllTenantIdsAsync()
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(SessionsTableName);
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

        // ===== RULE RESULTS METHODS =====

        /// <summary>
        /// Stores a rule evaluation result
        /// PartitionKey: {TenantId}_{SessionId}, RowKey: RuleId
        /// </summary>
        public async Task<bool> StoreRuleResultAsync(RuleResult result)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(RuleResultsTableName);
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
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(RuleResultsTableName);
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
                        MatchedConditions = DeserializeJson<Dictionary<string, object>>(entity.GetString("MatchedConditionsJson")),
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
                var tableClient = _tableServiceClient.GetTableClient(GatherRulesTableName);

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
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(GatherRulesTableName);
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
                var tableClient = _tableServiceClient.GetTableClient(GatherRulesTableName);
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

        // ===== ANALYZE RULES METHODS =====

        /// <summary>
        /// Stores or updates an analyze rule
        /// PartitionKey: TenantId (or "global" for built-in), RowKey: RuleId
        /// </summary>
        public async Task<bool> StoreAnalyzeRuleAsync(AnalyzeRule rule, string tenantId = "global")
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(AnalyzeRulesTableName);

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
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(AnalyzeRulesTableName);
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
                var tableClient = _tableServiceClient.GetTableClient(AnalyzeRulesTableName);
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

        // ===== USER ACTIVITY METHODS =====

        /// <summary>
        /// Records a user login activity
        /// PartitionKey: TenantId, RowKey: {invertedTicks}_{Guid} for reverse-chronological ordering
        /// </summary>
        public async Task RecordUserLoginAsync(string tenantId, string upn, string? displayName, string? objectId)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(UserActivityTableName);
                var now = DateTime.UtcNow;
                var invertedTicks = (DateTime.MaxValue.Ticks - now.Ticks).ToString("D20");

                var entity = new TableEntity(tenantId, $"{invertedTicks}_{Guid.NewGuid():N}")
                {
                    ["Upn"] = upn ?? string.Empty,
                    ["DisplayName"] = displayName ?? string.Empty,
                    ["ObjectId"] = objectId ?? string.Empty,
                    ["LoginAt"] = now
                };

                await tableClient.AddEntityAsync(entity);
                _logger.LogDebug($"Recorded login for {upn} in tenant {tenantId}");
            }
            catch (Exception ex)
            {
                // Don't fail the login if activity recording fails
                _logger.LogWarning(ex, $"Failed to record login activity for {upn}");
            }
        }

        /// <summary>
        /// Gets user activity metrics for a specific tenant
        /// </summary>
        public async Task<UserActivityMetrics> GetUserActivityMetricsAsync(string tenantId)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(UserActivityTableName);
                var query = tableClient.QueryAsync<TableEntity>(filter: $"PartitionKey eq '{tenantId}'");

                var now = DateTime.UtcNow;
                var today = now.Date;
                var last7Days = now.AddDays(-7);
                var last30Days = now.AddDays(-30);

                var allUpns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var todayUpns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var last7Upns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var last30Upns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int todayLogins = 0;

                await foreach (var entity in query)
                {
                    var upn = entity.GetString("Upn") ?? string.Empty;
                    var loginAt = entity.GetDateTime("LoginAt") ?? DateTime.MinValue;

                    if (string.IsNullOrEmpty(upn)) continue;

                    allUpns.Add(upn);

                    if (loginAt >= last30Days) last30Upns.Add(upn);
                    if (loginAt >= last7Days) last7Upns.Add(upn);
                    if (loginAt >= today)
                    {
                        todayUpns.Add(upn);
                        todayLogins++;
                    }
                }

                return new UserActivityMetrics
                {
                    TotalUniqueUsers = allUpns.Count,
                    DailyLogins = todayLogins,
                    ActiveUsersLast7Days = last7Upns.Count,
                    ActiveUsersLast30Days = last30Upns.Count
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get user activity metrics for tenant {tenantId}");
                return new UserActivityMetrics();
            }
        }

        /// <summary>
        /// Gets user activity metrics across all tenants (for galactic admin)
        /// </summary>
        public async Task<UserActivityMetrics> GetAllUserActivityMetricsAsync()
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(UserActivityTableName);
                var query = tableClient.QueryAsync<TableEntity>();

                var now = DateTime.UtcNow;
                var today = now.Date;
                var last7Days = now.AddDays(-7);
                var last30Days = now.AddDays(-30);

                var allUpns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var todayUpns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var last7Upns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var last30Upns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int todayLogins = 0;

                await foreach (var entity in query)
                {
                    var upn = entity.GetString("Upn") ?? string.Empty;
                    var loginAt = entity.GetDateTime("LoginAt") ?? DateTime.MinValue;

                    if (string.IsNullOrEmpty(upn)) continue;

                    allUpns.Add(upn);

                    if (loginAt >= last30Days) last30Upns.Add(upn);
                    if (loginAt >= last7Days) last7Upns.Add(upn);
                    if (loginAt >= today)
                    {
                        todayUpns.Add(upn);
                        todayLogins++;
                    }
                }

                return new UserActivityMetrics
                {
                    TotalUniqueUsers = allUpns.Count,
                    DailyLogins = todayLogins,
                    ActiveUsersLast7Days = last7Upns.Count,
                    ActiveUsersLast30Days = last30Upns.Count
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get all user activity metrics");
                return new UserActivityMetrics();
            }
        }

        /// <summary>
        /// Gets user login count for a specific date range (used by daily maintenance)
        /// </summary>
        public async Task<(int uniqueUsers, int loginCount)> GetUserActivityForDateAsync(string? tenantId, DateTime date)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(UserActivityTableName);
                var startOfDay = date.Date;
                var endOfDay = startOfDay.AddDays(1);

                string filter;
                if (!string.IsNullOrEmpty(tenantId) && tenantId != "global")
                {
                    filter = $"PartitionKey eq '{tenantId}' and LoginAt ge datetime'{startOfDay:yyyy-MM-ddTHH:mm:ss}Z' and LoginAt lt datetime'{endOfDay:yyyy-MM-ddTHH:mm:ss}Z'";
                }
                else
                {
                    filter = $"LoginAt ge datetime'{startOfDay:yyyy-MM-ddTHH:mm:ss}Z' and LoginAt lt datetime'{endOfDay:yyyy-MM-ddTHH:mm:ss}Z'";
                }

                var query = tableClient.QueryAsync<TableEntity>(filter: filter);

                var upns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int loginCount = 0;

                await foreach (var entity in query)
                {
                    var upn = entity.GetString("Upn") ?? string.Empty;
                    if (!string.IsNullOrEmpty(upn))
                    {
                        upns.Add(upn);
                        loginCount++;
                    }
                }

                return (upns.Count, loginCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get user activity for date {date:yyyy-MM-dd}");
                return (0, 0);
            }
        }

        // ===== HELPER METHODS =====

        private T DeserializeJson<T>(string? json) where T : new()
        {
            if (string.IsNullOrEmpty(json))
                return new T();

            try
            {
                return JsonConvert.DeserializeObject<T>(json) ?? new T();
            }
            catch
            {
                return new T();
            }
        }

        private string[] DeserializeJsonArray(string? json)
        {
            if (string.IsNullOrEmpty(json))
                return Array.Empty<string>();

            try
            {
                return JsonConvert.DeserializeObject<string[]>(json) ?? Array.Empty<string>();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }

    public class AuditLogEntry
    {
        public string Id { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string EntityType { get; set; } = string.Empty;
        public string EntityId { get; set; } = string.Empty;
        public string PerformedBy { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public string Details { get; set; } = string.Empty;
    }

    public class UserActivityMetrics
    {
        public int TotalUniqueUsers { get; set; }
        public int DailyLogins { get; set; }
        public int ActiveUsersLast7Days { get; set; }
        public int ActiveUsersLast30Days { get; set; }
    }
}
