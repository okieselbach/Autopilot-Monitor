using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Service for managing Azure Table Storage operations
    /// </summary>
    public class TableStorageService
    {
        private readonly TableServiceClient _tableServiceClient;
        private readonly ILogger<TableStorageService> _logger;
        private bool _tablesInitialized = false;
        private readonly object _initLock = new object();

        public TableStorageService(IConfiguration configuration, ILogger<TableStorageService> logger)
        {
            _logger = logger;
            var connectionString = configuration["AzureTableStorageConnectionString"];
            _tableServiceClient = new TableServiceClient(connectionString);
        }

        /// <summary>
        /// Initializes all Azure Table Storage tables.
        /// This method is idempotent and safe to call multiple times.
        /// Should be called at application startup.
        /// </summary>
        public async Task InitializeTablesAsync()
        {
            if (_tablesInitialized)
            {
                _logger.LogDebug("Tables already initialized, skipping");
                return;
            }

            lock (_initLock)
            {
                if (_tablesInitialized) return;
            }

            _logger.LogInformation("Initializing Azure Table Storage tables...");
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var successCount = 0;
            var failCount = 0;

            foreach (var tableName in Constants.TableNames.All)
            {
                try
                {
                    await _tableServiceClient.CreateTableIfNotExistsAsync(tableName);
                    _logger.LogDebug($"Table '{tableName}' initialized");
                    successCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to initialize table '{tableName}'");
                    failCount++;
                }
            }

            stopwatch.Stop();
            _logger.LogInformation($"Table initialization completed in {stopwatch.ElapsedMilliseconds}ms: {successCount} succeeded, {failCount} failed");

            lock (_initLock)
            {
                _tablesInitialized = failCount == 0;
            }
        }

        /// <summary>
        /// Gets the TableServiceClient for direct access (used by other services)
        /// </summary>
        public TableServiceClient GetTableServiceClient() => _tableServiceClient;

        /// <summary>
        /// Stores a session registration
        /// </summary>
        public async Task<bool> StoreSessionAsync(SessionRegistration registration)
        {
            SecurityValidator.EnsureValidGuid(registration.TenantId, "TenantId");
            SecurityValidator.EnsureValidGuid(registration.SessionId, "SessionId");

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);

                // If the agent restarts with the same session ID, preserve timeline/progress fields
                // from the existing session row instead of resetting them to "fresh start".
                DateTime startedAt = registration.StartedAt;
                int currentPhase = (int)EnrollmentPhase.Start;
                string status = SessionStatus.InProgress.ToString();
                int eventCount = 0;
                DateTime? completedAt = null;
                string failureReason = string.Empty;

                try
                {
                    var existing = await tableClient.GetEntityAsync<TableEntity>(registration.TenantId, registration.SessionId);
                    var existingEntity = existing.Value;

                    var existingStartedAt = existingEntity.GetDateTimeOffset("StartedAt")?.UtcDateTime;
                    if (existingStartedAt.HasValue && existingStartedAt.Value < startedAt)
                        startedAt = existingStartedAt.Value;

                    currentPhase = existingEntity.GetInt32("CurrentPhase") ?? currentPhase;
                    status = existingEntity.GetString("Status") ?? status;
                    eventCount = existingEntity.GetInt32("EventCount") ?? eventCount;
                    completedAt = existingEntity.GetDateTimeOffset("CompletedAt")?.UtcDateTime;
                    failureReason = existingEntity.GetString("FailureReason") ?? string.Empty;
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    // New session row - use defaults above.
                }

                // If events were ingested before session registration succeeded, align StartedAt
                // with the earliest event we already have for this session.
                var earliestEventTimestamp = await GetEarliestSessionEventTimestampAsync(registration.TenantId, registration.SessionId);
                if (earliestEventTimestamp.HasValue && earliestEventTimestamp.Value < startedAt)
                {
                    startedAt = earliestEventTimestamp.Value;
                }

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
                    ["StartedAt"] = startedAt,
                    ["AgentVersion"] = registration.AgentVersion ?? string.Empty,
                    ["EnrollmentType"] = registration.EnrollmentType ?? "v1",
                    ["CurrentPhase"] = currentPhase,
                    ["Status"] = status,
                    ["EventCount"] = eventCount
                };

                if (completedAt.HasValue)
                    entity["CompletedAt"] = completedAt.Value;

                if (!string.IsNullOrWhiteSpace(failureReason))
                    entity["FailureReason"] = failureReason;

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
            SecurityValidator.EnsureValidGuid(evt.TenantId, "TenantId");
            SecurityValidator.EnsureValidGuid(evt.SessionId, "SessionId");

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Events);

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
        /// Stores multiple events as batch transactions (Entity Group Transactions).
        /// All events must share the same PartitionKey (TenantId_SessionId).
        /// Azure Table Storage allows max 100 entities per transaction.
        /// Falls back to individual writes if a batch fails.
        /// </summary>
        /// <returns>List of successfully stored events</returns>
        public async Task<List<EnrollmentEvent>> StoreEventsBatchAsync(List<EnrollmentEvent> events)
        {
            if (events == null || events.Count == 0)
                return new List<EnrollmentEvent>();

            // Validate all events upfront
            foreach (var evt in events)
            {
                SecurityValidator.EnsureValidGuid(evt.TenantId, "TenantId");
                SecurityValidator.EnsureValidGuid(evt.SessionId, "SessionId");
            }

            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Events);
            var storedEvents = new List<EnrollmentEvent>();

            // Group by PartitionKey (should be the same for all events in a request, but be safe)
            var groups = events.GroupBy(e => $"{e.TenantId}_{e.SessionId}");

            foreach (var group in groups)
            {
                // Chunk into batches of 100 (Azure Table Storage limit)
                var chunks = group.Select((evt, index) => new { evt, index })
                    .GroupBy(x => x.index / 100)
                    .Select(g => g.Select(x => x.evt).ToList());

                foreach (var chunk in chunks)
                {
                    try
                    {
                        var actions = chunk.Select(evt =>
                        {
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

                            return new TableTransactionAction(TableTransactionActionType.UpsertReplace, entity);
                        }).ToList();

                        await tableClient.SubmitTransactionAsync(actions);
                        storedEvents.AddRange(chunk);
                        _logger.LogDebug($"Batch stored {chunk.Count} events for partition {group.Key}");
                    }
                    catch (Exception ex)
                    {
                        // Batch failed - fall back to individual writes for this chunk
                        _logger.LogWarning(ex, $"Batch write failed for {chunk.Count} events, falling back to individual writes");

                        foreach (var evt in chunk)
                        {
                            if (await StoreEventAsync(evt))
                            {
                                storedEvents.Add(evt);
                            }
                        }
                    }
                }
            }

            return storedEvents;
        }

        /// <summary>
        /// Gets all sessions for a tenant
        /// </summary>
        public async Task<List<SessionSummary>> GetSessionsAsync(string tenantId, int maxResults = 100)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);
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
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);
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
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);
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
        public async Task<bool> UpdateSessionStatusAsync(string tenantId, string sessionId, SessionStatus status, EnrollmentPhase? currentPhase = null, string? failureReason = null, DateTime? completedAt = null, DateTime? earliestEventTimestamp = null)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));

            const int maxRetries = 3;
            int retryCount = 0;

            while (retryCount < maxRetries)
            {
                try
                {
                    var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);

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

                    var currentStartedAt = session.GetDateTimeOffset("StartedAt")?.UtcDateTime ?? DateTime.MaxValue;
                    var effectiveEarliest = earliestEventTimestamp;

                    // Self-heal StartedAt by also looking at persisted events in case an older event
                    // arrived in a previous request/batch.
                    var earliestStoredEventTimestamp = await GetEarliestSessionEventTimestampAsync(tenantId, sessionId);
                    if (earliestStoredEventTimestamp.HasValue &&
                        (!effectiveEarliest.HasValue || earliestStoredEventTimestamp.Value < effectiveEarliest.Value))
                    {
                        effectiveEarliest = earliestStoredEventTimestamp.Value;
                    }

                    if (effectiveEarliest.HasValue && effectiveEarliest.Value < currentStartedAt)
                    {
                        session["StartedAt"] = effectiveEarliest.Value;
                    }

                    // Set completion time if succeeded or failed
                    if (status == SessionStatus.Succeeded || status == SessionStatus.Failed)
                    {
                        // Use the provided completedAt timestamp (from event) if available, otherwise use current time
                        session["CompletedAt"] = completedAt ?? DateTime.UtcNow;
                    }

                    // Set failure reason if failed
                    if (status == SessionStatus.Failed && !string.IsNullOrEmpty(failureReason))
                    {
                        session["FailureReason"] = failureReason;
                    }

                    // Update event count (full recount for accuracy on status changes)
                    var eventsTableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Events);
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
        /// Increments the session event count without touching status or phase fields.
        /// Uses Merge mode to safely handle concurrent updates.
        /// </summary>
        public async Task IncrementSessionEventCountAsync(string tenantId, string sessionId, int increment, DateTime? earliestEventTimestamp = null)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));

            const int maxRetries = 3;
            int retryCount = 0;

            while (retryCount < maxRetries)
            {
                try
                {
                    var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);
                    var entity = await tableClient.GetEntityAsync<TableEntity>(tenantId, sessionId);
                    var currentCount = entity.Value.GetInt32("EventCount") ?? 0;

                    var update = new TableEntity(tenantId, sessionId)
                    {
                        ["EventCount"] = currentCount + increment
                    };

                    var currentStartedAt = entity.Value.GetDateTimeOffset("StartedAt")?.UtcDateTime ?? DateTime.MaxValue;
                    var effectiveEarliest = earliestEventTimestamp;

                    // Self-heal StartedAt by checking already persisted events for this session.
                    var earliestStoredEventTimestamp = await GetEarliestSessionEventTimestampAsync(tenantId, sessionId);
                    if (earliestStoredEventTimestamp.HasValue &&
                        (!effectiveEarliest.HasValue || earliestStoredEventTimestamp.Value < effectiveEarliest.Value))
                    {
                        effectiveEarliest = earliestStoredEventTimestamp.Value;
                    }

                    if (effectiveEarliest.HasValue && effectiveEarliest.Value < currentStartedAt)
                    {
                        update["StartedAt"] = effectiveEarliest.Value;
                    }

                    await tableClient.UpdateEntityAsync(update, entity.Value.ETag, TableUpdateMode.Merge);
                    return;
                }
                catch (Azure.RequestFailedException ex) when (ex.Status == 412)
                {
                    retryCount++;
                    if (retryCount >= maxRetries)
                    {
                        _logger.LogWarning($"Failed to increment event count for session {sessionId} after {maxRetries} retries due to ETag conflicts");
                        return;
                    }
                    await Task.Delay(50 * (int)Math.Pow(2, retryCount - 1));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to increment event count for session {sessionId}");
                    return;
                }
            }
        }

        /// <summary>
        /// Gets all events for a specific session
        /// </summary>
        public async Task<List<EnrollmentEvent>> GetSessionEventsAsync(string tenantId, string sessionId, int maxResults = 1000)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Events);
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
                Data = DeserializeEventData(entity.GetString("DataJson"))
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
                    : (int)(DateTime.UtcNow - startedAt).TotalSeconds,
                EnrollmentType = entity.GetString("EnrollmentType") ?? "v1"
            };
        }

        /// <summary>
        /// Deletes a session from storage
        /// </summary>
        public async Task<bool> DeleteSessionAsync(string tenantId, string sessionId)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);
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

        // ===== HISTORICAL METRICS METHODS =====

        /// <summary>
        /// Saves a historical metrics snapshot
        /// </summary>
        public async Task<bool> SaveUsageMetricsSnapshotAsync(UsageMetricsSnapshot metrics)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.UsageMetrics);

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
            if (!string.IsNullOrEmpty(tenantId))
                SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.UsageMetrics);

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

        /// <summary>
        /// Checks if a global usage metrics snapshot exists for a given date.
        /// Used by maintenance catch-up to determine which dates need aggregation.
        /// </summary>
        public async Task<bool> HasUsageMetricsSnapshotAsync(string date)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.UsageMetrics);
                await tableClient.GetEntityAsync<TableEntity>(date, "global");
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to check usage metrics snapshot for {date}");
                return false;
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

        // ===== APP INSTALL SUMMARIES METHODS =====

        /// <summary>
        /// Stores or updates an app install summary.
        /// Merges with any existing record so StartedAt is never overwritten with a later timestamp.
        /// PartitionKey: TenantId, RowKey: {SessionId}_{AppName}
        /// </summary>
        public async Task<bool> StoreAppInstallSummaryAsync(AppInstallSummary summary)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.AppInstallSummaries);
                var rowKey = $"{summary.SessionId}_{summary.AppName}";

                // Merge with existing record to preserve StartedAt from a prior batch
                try
                {
                    var existing = await tableClient.GetEntityAsync<TableEntity>(summary.TenantId, rowKey);
                    var existingStartedAt = existing.Value.GetDateTimeOffset("StartedAt")?.UtcDateTime;
                    if (existingStartedAt.HasValue && existingStartedAt.Value != DateTime.MinValue)
                    {
                        // Keep the earlier StartedAt; recalculate duration if CompletedAt is now known
                        if (summary.StartedAt == DateTime.MinValue || existingStartedAt.Value < summary.StartedAt)
                        {
                            summary.StartedAt = existingStartedAt.Value;
                            if (summary.CompletedAt.HasValue && summary.DurationSeconds == 0)
                            {
                                summary.DurationSeconds = (int)(summary.CompletedAt.Value - summary.StartedAt).TotalSeconds;
                            }
                        }
                    }
                }
                catch (Azure.RequestFailedException ex) when (ex.Status == 404)
                {
                    // No existing record – nothing to merge
                }

                var entity = new TableEntity(summary.TenantId, rowKey)
                {
                    ["AppName"] = summary.AppName ?? string.Empty,
                    ["SessionId"] = summary.SessionId ?? string.Empty,
                    ["TenantId"] = summary.TenantId ?? string.Empty,
                    ["Status"] = summary.Status ?? "InProgress",
                    ["DurationSeconds"] = summary.DurationSeconds,
                    ["DownloadBytes"] = summary.DownloadBytes,
                    ["DownloadDurationSeconds"] = summary.DownloadDurationSeconds,
                    ["FailureCode"] = summary.FailureCode ?? string.Empty,
                    ["FailureMessage"] = summary.FailureMessage ?? string.Empty,
                    ["StartedAt"] = summary.StartedAt,
                    ["CompletedAt"] = summary.CompletedAt
                };

                await tableClient.UpsertEntityAsync(entity);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to store app install summary for {summary.AppName}");
                return false;
            }
        }

        /// <summary>
        /// Gets all app install summaries for a tenant (fleet-level metrics)
        /// </summary>
        public async Task<List<AppInstallSummary>> GetAppInstallSummariesByTenantAsync(string tenantId)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.AppInstallSummaries);
                var query = tableClient.QueryAsync<TableEntity>(filter: $"PartitionKey eq '{tenantId}'");

                var summaries = new List<AppInstallSummary>();
                await foreach (var entity in query)
                {
                    summaries.Add(MapToAppInstallSummary(entity));
                }

                return summaries;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get app install summaries for tenant {tenantId}");
                return new List<AppInstallSummary>();
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

        private AppInstallSummary MapToAppInstallSummary(TableEntity entity)
        {
            return new AppInstallSummary
            {
                AppName = entity.GetString("AppName") ?? string.Empty,
                SessionId = entity.GetString("SessionId") ?? string.Empty,
                TenantId = entity.GetString("TenantId") ?? entity.PartitionKey,
                Status = entity.GetString("Status") ?? "InProgress",
                DurationSeconds = entity.GetInt32("DurationSeconds") ?? 0,
                DownloadBytes = entity.GetInt64("DownloadBytes") ?? 0,
                DownloadDurationSeconds = entity.GetInt32("DownloadDurationSeconds") ?? 0,
                FailureCode = entity.GetString("FailureCode") ?? string.Empty,
                FailureMessage = entity.GetString("FailureMessage") ?? string.Empty,
                StartedAt = entity.GetDateTimeOffset("StartedAt")?.UtcDateTime ?? DateTime.MinValue,
                CompletedAt = entity.GetDateTimeOffset("CompletedAt")?.UtcDateTime
            };
        }

        // ===== PLATFORM STATS METHODS =====

        /// <summary>
        /// Gets the current platform stats (single row: global/current)
        /// </summary>
        public async Task<PlatformStats?> GetPlatformStatsAsync()
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.PlatformStats);
                var response = await tableClient.GetEntityAsync<TableEntity>("global", "current");
                var entity = response.Value;

                return new PlatformStats
                {
                    TotalEnrollments = entity.GetInt64("TotalEnrollments") ?? 0,
                    TotalUsers = entity.GetInt64("TotalUsers") ?? 0,
                    TotalTenants = entity.GetInt64("TotalTenants") ?? 0,
                    UniqueDeviceModels = entity.GetInt64("UniqueDeviceModels") ?? 0,
                    TotalEventsProcessed = entity.GetInt64("TotalEventsProcessed") ?? 0,
                    SuccessfulEnrollments = entity.GetInt64("SuccessfulEnrollments") ?? 0,
                    IssuesDetected = entity.GetInt64("IssuesDetected") ?? 0,
                    LastFullCompute = entity.GetDateTimeOffset("LastFullCompute")?.UtcDateTime ?? DateTime.MinValue,
                    LastUpdated = entity.GetDateTimeOffset("LastUpdated")?.UtcDateTime ?? DateTime.MinValue
                };
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get platform stats");
                return null;
            }
        }

        /// <summary>
        /// Saves the full platform stats (upsert)
        /// </summary>
        public async Task<bool> SavePlatformStatsAsync(PlatformStats stats)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.PlatformStats);

                var entity = new TableEntity("global", "current")
                {
                    ["TotalEnrollments"] = stats.TotalEnrollments,
                    ["TotalUsers"] = stats.TotalUsers,
                    ["TotalTenants"] = stats.TotalTenants,
                    ["UniqueDeviceModels"] = stats.UniqueDeviceModels,
                    ["TotalEventsProcessed"] = stats.TotalEventsProcessed,
                    ["SuccessfulEnrollments"] = stats.SuccessfulEnrollments,
                    ["IssuesDetected"] = stats.IssuesDetected,
                    ["LastFullCompute"] = stats.LastFullCompute,
                    ["LastUpdated"] = stats.LastUpdated
                };

                await tableClient.UpsertEntityAsync(entity);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save platform stats");
                return false;
            }
        }

        /// <summary>
        /// Increments a specific platform stat counter atomically.
        /// Reads current value, increments, and writes back.
        /// </summary>
        public async Task IncrementPlatformStatAsync(string field, long amount = 1)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.PlatformStats);

                TableEntity entity;

                try
                {
                    var response = await tableClient.GetEntityAsync<TableEntity>("global", "current");
                    entity = response.Value;
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    entity = new TableEntity("global", "current")
                    {
                        ["TotalEnrollments"] = 0L,
                        ["TotalUsers"] = 0L,
                        ["TotalTenants"] = 0L,
                        ["UniqueDeviceModels"] = 0L,
                        ["TotalEventsProcessed"] = 0L,
                        ["SuccessfulEnrollments"] = 0L,
                        ["IssuesDetected"] = 0L,
                        ["LastFullCompute"] = DateTime.MinValue,
                        ["LastUpdated"] = DateTime.UtcNow
                    };
                }

                var current = entity.GetInt64(field) ?? 0;
                entity[field] = current + amount;
                entity["LastUpdated"] = DateTime.UtcNow;

                await tableClient.UpsertEntityAsync(entity);
            }
            catch (Exception ex)
            {
                // Non-fatal: don't break the caller if stats update fails
                _logger.LogWarning(ex, $"Failed to increment platform stat {field}");
            }
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
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.UserActivity);
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
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.UserActivity);
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
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.UserActivity);
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
            if (!string.IsNullOrEmpty(tenantId) && tenantId != "global")
                SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.UserActivity);
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

        /// <summary>
        /// Deserializes MatchedConditions JSON and normalizes nested JObject/JArray values
        /// to plain Dictionary/List so System.Text.Json can serialize them correctly.
        /// </summary>
        private Dictionary<string, object> DeserializeMatchedConditions(string? json)
        {
            if (string.IsNullOrEmpty(json))
                return new Dictionary<string, object>();

            try
            {
                var raw = JsonConvert.DeserializeObject<Dictionary<string, object>>(json)
                          ?? new Dictionary<string, object>();

                var result = new Dictionary<string, object>();
                foreach (var kv in raw)
                    result[kv.Key] = NormalizeJToken(kv.Value);
                return result;
            }
            catch
            {
                return new Dictionary<string, object>();
            }
        }

        private static object NormalizeJToken(object? value)
        {
            if (value is Newtonsoft.Json.Linq.JObject jObj)
            {
                var dict = new Dictionary<string, object>();
                foreach (var prop in jObj.Properties())
                    dict[prop.Name] = NormalizeJToken(prop.Value);
                return dict;
            }
            if (value is Newtonsoft.Json.Linq.JArray jArr)
            {
                var list = new List<object>();
                foreach (var item in jArr)
                    list.Add(NormalizeJToken(item));
                return list;
            }
            if (value is Newtonsoft.Json.Linq.JValue jVal)
                return jVal.Value ?? string.Empty;
            return value ?? string.Empty;
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

        /// <summary>
        /// Returns the earliest event timestamp persisted for a session, if any.
        /// Events are written with RowKey "{Timestamp}_{Sequence}", so querying the partition
        /// and taking the first row yields the earliest event.
        /// </summary>
        private async Task<DateTime?> GetEarliestSessionEventTimestampAsync(string tenantId, string sessionId)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Events);
                var partitionKey = $"{tenantId}_{sessionId}";
                var query = tableClient.QueryAsync<TableEntity>(
                    filter: $"PartitionKey eq '{partitionKey}'",
                    maxPerPage: 1,
                    select: new[] { "Timestamp", "RowKey" }
                );

                await foreach (var entity in query)
                {
                    return entity.GetDateTimeOffset("Timestamp")?.UtcDateTime
                           ?? entity.GetDateTime("Timestamp");
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, $"Could not determine earliest event timestamp for session {sessionId}");
            }

            return null;
        }

        /// <summary>
        /// Deserializes event data JSON and converts JToken objects to native .NET types
        /// </summary>
        private Dictionary<string, object> DeserializeEventData(string? dataJson)
        {
            if (string.IsNullOrEmpty(dataJson))
                return new Dictionary<string, object>();

            try
            {
                var deserialized = JsonConvert.DeserializeObject<Dictionary<string, object>>(dataJson);
                if (deserialized == null)
                    return new Dictionary<string, object>();

                // Convert all JToken values to native types
                var result = new Dictionary<string, object>();
                foreach (var kvp in deserialized)
                {
                    result[kvp.Key] = ConvertJTokenToNative(kvp.Value);
                }
                return result;
            }
            catch
            {
                return new Dictionary<string, object>();
            }
        }

        /// <summary>
        /// Converts JToken objects (JArray, JObject) to native .NET types
        /// This fixes the issue where Newtonsoft.Json deserialization creates JToken objects
        /// that get serialized incorrectly as nested empty arrays
        /// </summary>
        private object ConvertJTokenToNative(object value)
        {
            if (value is JArray jArray)
            {
                return jArray.Select(item => ConvertJTokenToNative(item)).ToList();
            }
            else if (value is JObject jObject)
            {
                var dict = new Dictionary<string, object>();
                foreach (var prop in jObject.Properties())
                {
                    dict[prop.Name] = ConvertJTokenToNative(prop.Value);
                }
                return dict;
            }
            else if (value is JValue jValue)
            {
                return jValue.Value ?? string.Empty;
            }
            return value;
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
