using Azure;
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
        // ===== SESSION MANAGEMENT METHODS =====

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

                    // WhiteGlove resumption: if the existing session was in Pending state,
                    // this re-registration means the user has received the device and booted it.
                    // Transition back to InProgress for Part 2 of enrollment.
                    if (status == SessionStatus.Pending.ToString())
                    {
                        _logger.LogInformation($"Session {registration.SessionId} resuming from Pending (WhiteGlove Part 2)");
                        status = SessionStatus.InProgress.ToString();
                    }
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

        // ===== EVENT MANAGEMENT METHODS =====

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
        public async Task<List<SessionSummary>> GetSessionsAsync(string tenantId, int maxResults = 100, DateTime? since = null)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);
                var sessions = new List<SessionSummary>();

                // Query sessions by tenant (PartitionKey), optionally filtered by StartedAt
                // maxPerPage capped at 1000 (Azure Table Storage limit), pagination is automatic
                var filter = $"PartitionKey eq '{tenantId}'";
                if (since.HasValue)
                {
                    filter += $" and StartedAt ge datetime'{since.Value:yyyy-MM-ddTHH:mm:ss.fffffffZ}'";
                }

                var query = tableClient.QueryAsync<TableEntity>(
                    filter: filter,
                    maxPerPage: Math.Min(maxResults, 1000)
                );

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
                _logger.LogError("Failed to get sessions for tenant {TenantId}: {ExType}: {ExMessage}\n{StackTrace}",
                    tenantId, ex.GetType().Name, ex.Message, ex.StackTrace);
                return new List<SessionSummary>();
            }
        }

        /// <summary>
        /// Gets all sessions across all tenants (for galactic admin mode)
        /// </summary>
        public async Task<List<SessionSummary>> GetAllSessionsAsync(int maxResults = 100, DateTime? since = null)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);
                var sessions = new List<SessionSummary>();

                // Query all sessions, optionally filtered by StartedAt to limit dataset size
                // maxPerPage is capped at 1000 (Azure Table Storage limit); pagination is automatic
                string? filter = since.HasValue
                    ? $"StartedAt ge datetime'{since.Value:yyyy-MM-ddTHH:mm:ss.fffffffZ}'"
                    : null;

                var query = tableClient.QueryAsync<TableEntity>(
                    filter: filter,
                    maxPerPage: Math.Min(maxResults, 1000)
                );

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
        /// Updates the session status and current phase.
        /// Uses Merge mode to write only changed fields, reducing ETag conflicts under concurrency.
        /// The caller (IngestEventsFunction) provides earliestEventTimestamp from the current batch;
        /// no redundant Events-table scan is performed here.
        /// Event count is maintained atomically by IncrementSessionEventCountAsync and is not
        /// recounted here — avoiding an expensive full-partition scan on every status change.
        /// </summary>
        public async Task<bool> UpdateSessionStatusAsync(string tenantId, string sessionId, SessionStatus status, EnrollmentPhase? currentPhase = null, string? failureReason = null, DateTime? completedAt = null, DateTime? earliestEventTimestamp = null, DateTime? latestEventTimestamp = null)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));

            const int maxRetries = 5;
            int retryCount = 0;

            while (retryCount < maxRetries)
            {
                try
                {
                    var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);

                    // Read the existing entity to check idempotency guards and compute derived fields
                    var entity = await tableClient.GetEntityAsync<TableEntity>(tenantId, sessionId);
                    var session = entity.Value;

                    // Idempotency: if the session is already in a terminal state (Succeeded/Failed),
                    // do not overwrite it with another terminal state to prevent duplicate notifications.
                    var existingStatusStr = session.GetString("Status");
                    if (status == SessionStatus.Succeeded || status == SessionStatus.Failed)
                    {
                        if (existingStatusStr == SessionStatus.Succeeded.ToString() || existingStatusStr == SessionStatus.Failed.ToString())
                        {
                            _logger.LogInformation($"Session {sessionId} already in terminal state '{existingStatusStr}', skipping status update to '{status}'");
                            return false;
                        }
                    }

                    // Build a Merge update with only the fields that actually change
                    var update = new TableEntity(tenantId, sessionId);

                    // Status promotion rule: a Pending session (WhiteGlove pre-provisioning complete)
                    // must not regress to InProgress via phase-change events from the resumed boot.
                    // The Pending → InProgress transition happens exclusively in StoreSessionAsync
                    // (agent re-registration at Boot 2).
                    if (status == SessionStatus.InProgress && existingStatusStr == SessionStatus.Pending.ToString())
                    {
                        _logger.LogInformation($"Session {sessionId} is Pending (WhiteGlove), preserving status (InProgress blocked by promotion rule)");
                        // Phase, timestamps are still updated below.
                    }
                    else
                    {
                        update["Status"] = status.ToString();
                    }

                    // Update current phase if provided
                    if (currentPhase.HasValue)
                    {
                        update["CurrentPhase"] = (int)currentPhase.Value;
                    }

                    // Align StartedAt with the earliest event timestamp provided by the caller
                    var currentStartedAt = session.GetDateTimeOffset("StartedAt")?.UtcDateTime ?? DateTime.MaxValue;
                    if (earliestEventTimestamp.HasValue && earliestEventTimestamp.Value < currentStartedAt)
                    {
                        update["StartedAt"] = earliestEventTimestamp.Value;
                    }

                    // Set completion time if succeeded or failed
                    if (status == SessionStatus.Succeeded || status == SessionStatus.Failed)
                    {
                        // Use the provided completedAt timestamp (from event) if available, otherwise use current time
                        var effectiveCompletedAt = completedAt ?? DateTime.UtcNow;
                        update["CompletedAt"] = effectiveCompletedAt;

                        // Store accurate DurationSeconds based on first event timestamp (not registration StartedAt)
                        // The agent registers before events arrive, so registration StartedAt is always earlier
                        // than the actual enrollment start — using the first event gives the true enrollment duration.
                        // Use the effective StartedAt (already corrected by earlier batches via the StartedAt
                        // alignment above), taking the minimum with the current batch's earliest timestamp.
                        var effectiveStartedAt = earliestEventTimestamp.HasValue && earliestEventTimestamp.Value < currentStartedAt
                            ? earliestEventTimestamp.Value
                            : currentStartedAt;
                        if (effectiveStartedAt < effectiveCompletedAt)
                            update["DurationSeconds"] = (int)(effectiveCompletedAt - effectiveStartedAt).TotalSeconds;
                    }

                    // Track the most recent event timestamp for excessive data sender detection
                    if (latestEventTimestamp.HasValue)
                    {
                        var currentLastEventAt = session.GetDateTimeOffset("LastEventAt")?.UtcDateTime;
                        if (!currentLastEventAt.HasValue || latestEventTimestamp.Value > currentLastEventAt.Value)
                            update["LastEventAt"] = latestEventTimestamp.Value;
                    }

                    // Set failure reason if failed
                    if (status == SessionStatus.Failed && !string.IsNullOrEmpty(failureReason))
                    {
                        update["FailureReason"] = failureReason;
                    }

                    // Merge mode: only the fields set above are written; all other fields remain untouched.
                    // This drastically reduces ETag conflicts when concurrent requests update different fields.
                    await tableClient.UpdateEntityAsync(update, session.ETag, TableUpdateMode.Merge);

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

                    // Exponential backoff with jitter to decorrelate concurrent retries
                    var baseDelay = 50 * (int)Math.Pow(2, retryCount - 1);
                    await Task.Delay(baseDelay + Random.Shared.Next(0, baseDelay));
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
        /// The caller provides earliestEventTimestamp from the current batch;
        /// no redundant Events-table scan is performed here.
        /// </summary>
        public async Task IncrementSessionEventCountAsync(string tenantId, string sessionId, int increment, DateTime? earliestEventTimestamp = null, DateTime? latestEventTimestamp = null)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));

            const int maxRetries = 5;
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

                    // Align StartedAt with the earliest event timestamp provided by the caller
                    var currentStartedAt = entity.Value.GetDateTimeOffset("StartedAt")?.UtcDateTime ?? DateTime.MaxValue;
                    if (earliestEventTimestamp.HasValue && earliestEventTimestamp.Value < currentStartedAt)
                    {
                        update["StartedAt"] = earliestEventTimestamp.Value;
                    }

                    // Track the most recent event timestamp for excessive data sender detection
                    if (latestEventTimestamp.HasValue)
                    {
                        var currentLastEventAt = entity.Value.GetDateTimeOffset("LastEventAt")?.UtcDateTime;
                        if (!currentLastEventAt.HasValue || latestEventTimestamp.Value > currentLastEventAt.Value)
                            update["LastEventAt"] = latestEventTimestamp.Value;
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
                    // Exponential backoff with jitter to decorrelate concurrent retries
                    var baseDelay = 50 * (int)Math.Pow(2, retryCount - 1);
                    await Task.Delay(baseDelay + Random.Shared.Next(0, baseDelay));
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
        /// Stores the diagnostics blob name on an existing session (Merge-mode, single field update).
        /// </summary>
        public async Task UpdateSessionDiagnosticsBlobAsync(string tenantId, string sessionId, string blobName)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);
                var entity = await tableClient.GetEntityAsync<TableEntity>(tenantId, sessionId);

                var update = new TableEntity(tenantId, sessionId)
                {
                    ["DiagnosticsBlobName"] = blobName
                };

                await tableClient.UpdateEntityAsync(update, entity.Value.ETag, Azure.Data.Tables.TableUpdateMode.Merge);
                _logger.LogInformation($"Stored diagnostics blob name for session {sessionId}: {blobName}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to store diagnostics blob name for session {sessionId}");
            }
        }

        /// <summary>
        /// Sets the IsPreProvisioned flag on an existing session (Merge-mode, single field update).
        /// </summary>
        public async Task SetSessionPreProvisionedAsync(string tenantId, string sessionId, bool isPreProvisioned)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);
                var entity = await tableClient.GetEntityAsync<TableEntity>(tenantId, sessionId);

                var update = new TableEntity(tenantId, sessionId)
                {
                    ["IsPreProvisioned"] = isPreProvisioned
                };

                await tableClient.UpdateEntityAsync(update, entity.Value.ETag, Azure.Data.Tables.TableUpdateMode.Merge);
                _logger.LogInformation($"Set IsPreProvisioned={isPreProvisioned} for session {sessionId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to set IsPreProvisioned for session {sessionId}");
            }
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

        // ===== SESSION/EVENT MAPPING HELPERS =====

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
                Timestamp = DateTime.SpecifyKind(
                    entity.GetDateTimeOffset("Timestamp")?.UtcDateTime
                    ?? entity.GetDateTime("Timestamp")
                    ?? DateTime.UtcNow, DateTimeKind.Utc),
                EventType = entity.GetString("EventType") ?? string.Empty,
                Severity = (EventSeverity)(entity.GetInt32("Severity") ?? 0),
                Source = entity.GetString("Source") ?? string.Empty,
                Phase = (EnrollmentPhase)(entity.GetInt32("Phase") ?? 0),
                Message = entity.GetString("Message") ?? string.Empty,
                Sequence = entity.GetInt64("Sequence") ?? 0,
                Data = DeserializeEventData(entity.GetString("DataJson")),
                RowKey = entity.RowKey
            };
        }

        /// <summary>
        /// Maps a TableEntity to SessionSummary
        /// </summary>
        private SessionSummary MapToSessionSummary(TableEntity entity)
        {
            // All typed getters (GetInt32, GetDateTime, etc.) throw InvalidOperationException
            // when a property exists but has a different type (e.g. legacy data stored as string
            // instead of int). Use safe helpers to handle type mismatches gracefully.
            var startedAt = SafeGetDateTime(entity, "StartedAt") ?? DateTime.UtcNow;
            var completedAt = SafeGetDateTime(entity, "CompletedAt");

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
                CurrentPhase = SafeGetInt32(entity, "CurrentPhase") ?? 0,
                CurrentPhaseDetail = entity.GetString("CurrentPhaseDetail") ?? string.Empty,
                Status = status,
                FailureReason = entity.GetString("FailureReason") ?? string.Empty,
                EventCount = SafeGetInt32(entity, "EventCount") ?? 0,
                DurationSeconds = SafeGetInt32(entity, "DurationSeconds") switch
                {
                    // Stored duration is valid — use it directly
                    int d when d > 0 => d,
                    // Duration missing or zero (bug from batch-split race) — recompute from timestamps
                    _ => completedAt.HasValue
                        ? (int)(completedAt.Value - startedAt).TotalSeconds
                        : (int)(DateTime.UtcNow - startedAt).TotalSeconds
                },
                EnrollmentType = entity.GetString("EnrollmentType") ?? "v1",
                DiagnosticsBlobName = entity.GetString("DiagnosticsBlobName"),
                LastEventAt = SafeGetDateTime(entity, "LastEventAt"),
                IsPreProvisioned = entity.GetBoolean("IsPreProvisioned") ?? false
            };
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
    }
}
