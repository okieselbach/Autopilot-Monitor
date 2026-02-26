using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Extensions.SignalRService;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Functions.Functions
{
    public class IngestEventsFunction
    {
        private readonly ILogger<IngestEventsFunction> _logger;
        private readonly TableStorageService _storageService;
        private readonly TenantConfigurationService _configService;
        private readonly RateLimitService _rateLimitService;
        private readonly AutopilotDeviceValidator _autopilotDeviceValidator;
        private readonly AnalyzeRuleService _analyzeRuleService;
        private readonly TeamsNotificationService _teamsNotificationService;
        private readonly BlockedDeviceService _blockedDeviceService;

        public IngestEventsFunction(
            ILogger<IngestEventsFunction> logger,
            TableStorageService storageService,
            TenantConfigurationService configService,
            RateLimitService rateLimitService,
            AutopilotDeviceValidator autopilotDeviceValidator,
            AnalyzeRuleService analyzeRuleService,
            TeamsNotificationService teamsNotificationService,
            BlockedDeviceService blockedDeviceService)
        {
            _logger = logger;
            _storageService = storageService;
            _configService = configService;
            _rateLimitService = rateLimitService;
            _autopilotDeviceValidator = autopilotDeviceValidator;
            _analyzeRuleService = analyzeRuleService;
            _teamsNotificationService = teamsNotificationService;
            _blockedDeviceService = blockedDeviceService;
        }

        [Function("IngestEvents")]
        public async Task<IngestEventsOutput> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "events/ingest")] HttpRequestData req)
        {
            try
            {
                // --- Security checks FIRST — before touching the request body ---

                // TenantId is available in the X-Tenant-Id header, so we can validate the request
                // before paying the cost of gzip decompression and JSON parsing.
                var tenantIdHeader = req.Headers.Contains("X-Tenant-Id")
                    ? req.Headers.GetValues("X-Tenant-Id").FirstOrDefault()
                    : null;

                if (string.IsNullOrEmpty(tenantIdHeader))
                {
                    return await CreateErrorResponse(req, HttpStatusCode.BadRequest, "X-Tenant-Id header is required");
                }

                // Validate request security (certificate, rate limit, hardware whitelist, serial number in autopilot)
                // sessionId is not yet known at this point — that's fine, it's optional for logging only.
                var (validation, errorResponse) = await req.ValidateSecurityAsync(
                    tenantIdHeader,
                    _configService,
                    _rateLimitService,
                    _autopilotDeviceValidator,
                    _logger
                );

                if (errorResponse != null)
                {
                    // Security validation failed - return before parsing the body
                    return new IngestEventsOutput
                    {
                        HttpResponse = errorResponse,
                        SignalRMessages = Array.Empty<SignalRMessageAction>()
                    };
                }

                // --- Device block check (after security, before body decompression) ---
                // Check if this device has been administratively blocked (e.g. rogue device sending excessive data).
                // We read the serial number from the header (same header used by AutopilotDeviceValidator).
                // Using HTTP 200 with DeviceBlocked=true so the agent does not trigger its auth-failure circuit breaker.
                var serialNumberHeader = req.Headers.Contains("X-Device-SerialNumber")
                    ? req.Headers.GetValues("X-Device-SerialNumber").FirstOrDefault()
                    : null;

                if (!string.IsNullOrEmpty(serialNumberHeader))
                {
                    var (isBlocked, unblockAt) = await _blockedDeviceService.IsBlockedAsync(tenantIdHeader, serialNumberHeader);
                    if (isBlocked)
                    {
                        _logger.LogWarning(
                            "Rejected ingest from blocked device: TenantId={TenantId}, SerialNumber={SerialNumber}, UnblockAt={UnblockAt}",
                            tenantIdHeader, serialNumberHeader, unblockAt);

                        var blockedHttpResponse = req.CreateResponse(System.Net.HttpStatusCode.OK);
                        await blockedHttpResponse.WriteAsJsonAsync(new IngestEventsResponse
                        {
                            Success = false,
                            DeviceBlocked = true,
                            UnblockAt = unblockAt,
                            Message = "Device is temporarily blocked by an administrator.",
                            ProcessedAt = DateTime.UtcNow
                        });
                        return new IngestEventsOutput
                        {
                            HttpResponse = blockedHttpResponse,
                            SignalRMessages = Array.Empty<SignalRMessageAction>()
                        };
                    }
                }

                // --- Parse NDJSON+gzip request body (only after security is cleared) ---
                var request = await ParseNdjsonGzipRequest(req.Body, tenantIdHeader);

                if (request?.Events == null || request.Events.Count == 0)
                {
                    return await CreateErrorResponse(req, HttpStatusCode.BadRequest, "No events provided");
                }

                if (string.IsNullOrEmpty(request.SessionId) || string.IsNullOrEmpty(request.TenantId))
                {
                    return await CreateErrorResponse(req, HttpStatusCode.BadRequest, "SessionId and TenantId are required");
                }

                // Ensure body TenantId matches the validated header TenantId (prevent body spoofing)
                if (!string.Equals(request.TenantId, tenantIdHeader, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("TenantId mismatch: header={HeaderTenantId}, body={BodyTenantId}", tenantIdHeader, request.TenantId);
                    return await CreateErrorResponse(req, HttpStatusCode.Forbidden, "TenantId mismatch between header and payload");
                }

                var sessionPrefix = $"[Session: {request.SessionId.Substring(0, Math.Min(8, request.SessionId.Length))}]";
                _logger.LogInformation($"{sessionPrefix} IngestEvents: Processing {request.Events.Count} events (Device: {validation.CertificateThumbprint}, Hardware: {validation.Manufacturer} {validation.Model}, Rate: {validation.RateLimitResult?.RequestsInWindow}/{validation.RateLimitResult?.MaxRequests})");

                // Store events in Azure Table Storage (batch write for efficiency)
                var storedEvents = await _storageService.StoreEventsBatchAsync(request.Events);
                int processedCount = storedEvents.Count;

                EnrollmentEvent? lastPhaseChangeEvent = null;
                EnrollmentEvent? completionEvent = null;
                EnrollmentEvent? failureEvent = null;
                EnrollmentEvent? gatherCompletionEvent = null;
                EnrollmentEvent? diagnosticsUploadedEvent = null;
                string? failureReason = null;
                DateTime? earliestEventTimestamp = null;
                DateTime? latestEventTimestamp = null;

                // Track app install events for AppInstallSummary aggregation
                var appInstallUpdates = new Dictionary<string, AppInstallAggregationState>(StringComparer.OrdinalIgnoreCase);

                foreach (var evt in storedEvents)
                {
                    // Track earliest event timestamp for accurate session StartedAt
                    if (!earliestEventTimestamp.HasValue || evt.Timestamp < earliestEventTimestamp.Value)
                    {
                        earliestEventTimestamp = evt.Timestamp;
                    }

                    // Track latest event timestamp for excessive data sender detection
                    if (!latestEventTimestamp.HasValue || evt.Timestamp > latestEventTimestamp.Value)
                    {
                        latestEventTimestamp = evt.Timestamp;
                    }

                    // Track special events for session status updates
                    if (evt.EventType == "phase_changed" || evt.EventType == "esp_phase_changed")
                    {
                        lastPhaseChangeEvent = evt;
                    }
                    else if (evt.EventType == "enrollment_complete")
                    {
                        completionEvent = evt;
                    }
                    else if (evt.EventType == "gather_rules_collection_completed")
                    {
                        gatherCompletionEvent = evt;
                    }
                    else if (evt.EventType == "enrollment_failed")
                    {
                        failureEvent = evt;
                    }
                    else if (evt.EventType == "diagnostics_uploaded")
                    {
                        diagnosticsUploadedEvent = evt;
                    }

                    // Track app install events for per-app metrics
                    AggregateAppInstallEvent(evt, request.TenantId, request.SessionId, appInstallUpdates);
                }

                // Store app install summaries
                foreach (var summary in appInstallUpdates.Values)
                {
                    await _storageService.StoreAppInstallSummaryAsync(summary.Summary);
                }

                // Update session status based on events
                // statusTransitioned = true only when this is the FIRST time the session reaches Succeeded/Failed
                // (UpdateSessionStatusAsync returns false if already in a terminal state — idempotency guard)
                bool statusTransitioned = false;
                if (completionEvent != null)
                {
                    // Enrollment completed successfully - use event timestamp for accurate duration
                    statusTransitioned = await _storageService.UpdateSessionStatusAsync(
                        request.TenantId,
                        request.SessionId,
                        SessionStatus.Succeeded,
                        completionEvent.Phase,
                        completedAt: completionEvent.Timestamp,
                        earliestEventTimestamp: earliestEventTimestamp,
                        latestEventTimestamp: latestEventTimestamp
                    );
                    _logger.LogInformation("{SessionPrefix} Status: Succeeded (transitioned={Transitioned})", sessionPrefix, statusTransitioned);
                }
                else if (failureEvent != null)
                {
                    // Enrollment failed - use event timestamp for accurate duration
                    failureReason = failureEvent.Data?.ContainsKey("errorCode") == true
                        ? $"{failureEvent.Message} ({failureEvent.Data["errorCode"]})"
                        : failureEvent.Message;

                    statusTransitioned = await _storageService.UpdateSessionStatusAsync(
                        request.TenantId,
                        request.SessionId,
                        SessionStatus.Failed,
                        failureEvent.Phase,
                        failureReason,
                        completedAt: failureEvent.Timestamp,
                        earliestEventTimestamp: earliestEventTimestamp,
                        latestEventTimestamp: latestEventTimestamp
                    );
                    _logger.LogWarning("{SessionPrefix} Status: Failed - {FailureReason} (transitioned={Transitioned})", sessionPrefix, failureReason, statusTransitioned);
                }
                else if (gatherCompletionEvent != null)
                {
                    // Gather rules collection completed — mark session as Succeeded.
                    // Does not trigger enrollment stats or Teams notifications (not a real enrollment).
                    await _storageService.UpdateSessionStatusAsync(
                        request.TenantId,
                        request.SessionId,
                        SessionStatus.Succeeded,
                        gatherCompletionEvent.Phase,
                        completedAt: gatherCompletionEvent.Timestamp,
                        earliestEventTimestamp: earliestEventTimestamp,
                        latestEventTimestamp: latestEventTimestamp
                    );
                    _logger.LogInformation("{SessionPrefix} Status: Succeeded (gather_rules)", sessionPrefix);
                }
                else if (lastPhaseChangeEvent != null)
                {
                    // Update current phase (still in progress)
                    await _storageService.UpdateSessionStatusAsync(
                        request.TenantId,
                        request.SessionId,
                        SessionStatus.InProgress,
                        lastPhaseChangeEvent.Phase,
                        earliestEventTimestamp: earliestEventTimestamp,
                        latestEventTimestamp: latestEventTimestamp
                    );
                }
                else if (processedCount > 0)
                {
                    // No status change — lightweight event count increment only
                    await _storageService.IncrementSessionEventCountAsync(
                        request.TenantId,
                        request.SessionId,
                        processedCount,
                        earliestEventTimestamp,
                        latestEventTimestamp
                    );
                }

                // Run full analysis when enrollment ends (cost-efficient: one pass over all events)
                var newRuleResults = new List<AutopilotMonitor.Shared.Models.RuleResult>();
                if (completionEvent != null || failureEvent != null)
                {
                    try
                    {
                        var ruleEngine = new RuleEngine(_analyzeRuleService, _storageService, _logger);
                        newRuleResults = await ruleEngine.AnalyzeSessionAsync(request.TenantId, request.SessionId);

                        foreach (var result in newRuleResults)
                        {
                            await _storageService.StoreRuleResultAsync(result);
                        }

                        if (newRuleResults.Count > 0)
                        {
                            _logger.LogInformation($"{sessionPrefix} Enrollment-end analysis: {newRuleResults.Count} issue(s) detected");
                        }
                    }
                    catch (Exception ruleEx)
                    {
                        _logger.LogWarning(ruleEx, $"{sessionPrefix} Enrollment-end analysis failed (non-fatal)");
                    }
                }

                // Increment platform stats (fire-and-forget, non-blocking)
                _ = _storageService.IncrementPlatformStatAsync("TotalEventsProcessed", processedCount)
                    .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException, "Fire-and-forget IncrementPlatformStatAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
                if (newRuleResults.Count > 0)
                    _ = _storageService.IncrementPlatformStatAsync("IssuesDetected", newRuleResults.Count)
                        .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException, "Fire-and-forget IncrementPlatformStatAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
                if (completionEvent != null)
                    _ = _storageService.IncrementPlatformStatAsync("SuccessfulEnrollments")
                        .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException, "Fire-and-forget IncrementPlatformStatAsync failed"), TaskContinuationOptions.OnlyOnFaulted);

                // Store diagnostics blob name on session (if agent uploaded a diagnostics package)
                if (diagnosticsUploadedEvent != null)
                {
                    var blobName = diagnosticsUploadedEvent.Data?.ContainsKey("blobName") == true
                        ? diagnosticsUploadedEvent.Data["blobName"]?.ToString()
                        : null;
                    if (!string.IsNullOrEmpty(blobName))
                    {
                        await _storageService.UpdateSessionDiagnosticsBlobAsync(
                            request.TenantId, request.SessionId, blobName);
                    }
                }

                // Retrieve updated session data to include in SignalR messages
                var updatedSession = await _storageService.GetSessionAsync(request.TenantId, request.SessionId);

                // Send Teams notification on enrollment completion (fire-and-forget, non-fatal)
                // Only send when statusTransitioned=true to prevent duplicates on retry/double-upload
                if (statusTransitioned && (completionEvent != null || failureEvent != null))
                {
                    var tenantConfig = await _configService.GetConfigurationAsync(request.TenantId);
                    if (!string.IsNullOrEmpty(tenantConfig.TeamsWebhookUrl))
                    {
                        var notifySuccess = completionEvent != null && tenantConfig.TeamsNotifyOnSuccess;
                        var notifyFailure = failureEvent != null && tenantConfig.TeamsNotifyOnFailure;
                        if (notifySuccess || notifyFailure)
                        {
                            var duration = updatedSession?.DurationSeconds != null
                                ? TimeSpan.FromSeconds(updatedSession.DurationSeconds.Value)
                                : (TimeSpan?)null;
                            _ = _teamsNotificationService.SendEnrollmentNotificationAsync(
                                tenantConfig.TeamsWebhookUrl,
                                updatedSession?.DeviceName,
                                updatedSession?.SerialNumber,
                                updatedSession?.Manufacturer,
                                updatedSession?.Model,
                                success: completionEvent != null,
                                failureReason: failureReason,
                                duration: duration);
                        }
                    }
                }

                var response = req.CreateResponse(HttpStatusCode.OK);
                var responseData = new IngestEventsResponse
                {
                    Success = true,
                    EventsReceived = request.Events.Count,
                    EventsProcessed = processedCount,
                    Message = $"Successfully stored {processedCount} of {request.Events.Count} events",
                    ProcessedAt = DateTime.UtcNow
                };

                await response.WriteAsJsonAsync(responseData);

                // Send SignalR notifications using Groups for multi-tenancy and cost optimization
                // Include session data so frontend doesn't need to fetch it (cost optimization)

                // 1. Summary notification for session list updates (tenant-specific only)
                // Only sent to clients in the tenant group - prevents cross-tenant data leaks
                // Galactic Admins only receive "newSession" events from RegisterSessionFunction,
                // not every event batch, to avoid flooding them with updates
                // Note: newRuleResults intentionally omitted - list views don't use them
                // Send only mutable fields as delta update (static fields like
                // serialNumber, deviceName, manufacturer, model, startedAt,
                // enrollmentType never change after registration)
                object? sessionDelta = updatedSession != null ? new {
                    updatedSession.CurrentPhase,
                    updatedSession.CurrentPhaseDetail,
                    updatedSession.Status,
                    updatedSession.FailureReason,
                    updatedSession.EventCount,
                    updatedSession.DurationSeconds,
                    updatedSession.CompletedAt,
                    updatedSession.DiagnosticsBlobName
                } : null;

                var summaryMessage = new SignalRMessageAction("newevents")
                {
                    GroupName = $"tenant-{request.TenantId}",
                    Arguments = new object[] { new {
                        sessionId = request.SessionId,
                        tenantId = request.TenantId,
                        eventCount = processedCount,
                        sessionUpdate = sessionDelta
                    } }
                };

                // 2. Signal for real-time event streaming on detail pages (session-specific)
                // Only sent to clients viewing this specific session - cost-efficient signal pattern:
                // frontend fetches full events from Table Storage on receipt, ensuring canonical truth
                // and eliminating SignalR gap issues. Events payload omitted intentionally.
                var slimRuleResults = newRuleResults.Count > 0
                    ? newRuleResults.Select(r => new {
                        r.ResultId,
                        r.RuleId,
                        r.RuleTitle,
                        r.Severity,
                        r.Category,
                        r.ConfidenceScore,
                        r.Explanation,
                        r.Remediation,
                        r.RelatedDocs,
                        r.MatchedConditions,
                        r.DetectedAt
                    }).ToList<object>()
                    : null;

                var eventsMessage = new SignalRMessageAction("eventStream")
                {
                    GroupName = $"session-{request.TenantId}-{request.SessionId}",
                    Arguments = new object[] { new {
                        sessionId = request.SessionId,
                        tenantId = request.TenantId,
                        newEventCount = processedCount,
                        session = updatedSession,
                        newRuleResults = slimRuleResults
                    } }
                };

                return new IngestEventsOutput
                {
                    HttpResponse = response,
                    SignalRMessages = new[] { summaryMessage, eventsMessage }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error ingesting events");
                return await CreateErrorResponse(req, HttpStatusCode.InternalServerError, "Internal server error");
            }
        }

        /// <summary>
        /// Parses NDJSON + gzip compressed request
        /// Format: First line is metadata (sessionId, tenantId), subsequent lines are events
        /// </summary>
        private async Task<IngestEventsRequest> ParseNdjsonGzipRequest(Stream body, string? tenantId = null)
        {
            // Get configuration for payload size limit (use default if tenantId not available yet)
            var config = !string.IsNullOrEmpty(tenantId)
                ? await _configService.GetConfigurationAsync(tenantId)
                : null;

            var maxPayloadSizeBytes = (config?.MaxNdjsonPayloadSizeMB ?? 5) * 1024 * 1024;

            // Decompress gzip with size limit protection
            using var decompressed = new MemoryStream();
            using (var gzip = new GZipStream(body, CompressionMode.Decompress, leaveOpen: true))
            {
                // Copy with size limit to prevent memory exhaustion attacks
                var buffer = new byte[8192];
                int bytesRead;
                long totalBytesRead = 0;

                while ((bytesRead = await gzip.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    totalBytesRead += bytesRead;

                    // Check if we've exceeded the maximum payload size
                    if (totalBytesRead > maxPayloadSizeBytes)
                    {
                        throw new InvalidOperationException(
                            $"NDJSON payload size exceeds maximum allowed size of {config?.MaxNdjsonPayloadSizeMB ?? 5} MB (decompressed). " +
                            $"Current size: {totalBytesRead / 1024.0 / 1024.0:F2} MB"
                        );
                    }

                    await decompressed.WriteAsync(buffer, 0, bytesRead);
                }

                _logger.LogDebug($"NDJSON payload decompressed: {totalBytesRead / 1024.0:F2} KB (limit: {maxPayloadSizeBytes / 1024.0 / 1024.0} MB)");
            }

            decompressed.Position = 0;
            var ndjson = await new StreamReader(decompressed, Encoding.UTF8).ReadToEndAsync();

            // Parse NDJSON (newline-delimited JSON)
            var lines = ndjson.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            if (lines.Length < 1)
            {
                throw new InvalidOperationException("NDJSON must contain at least metadata line");
            }

            // First line: metadata
            var metadata = JsonConvert.DeserializeObject<NdjsonMetadata>(lines[0]);
            if (metadata == null)
            {
                throw new InvalidOperationException("Failed to parse NDJSON metadata");
            }

            // Subsequent lines: events
            var events = new List<EnrollmentEvent>();
            for (int i = 1; i < lines.Length; i++)
            {
                var evt = JsonConvert.DeserializeObject<EnrollmentEvent>(lines[i]);
                if (evt != null)
                {
                    NormalizeEventData(evt);
                    events.Add(evt);
                }
            }

            return new IngestEventsRequest
            {
                SessionId = metadata.SessionId,
                TenantId = metadata.TenantId,
                Events = events
            };
        }

        /// <summary>
        /// Normalizes event Data dictionary by converting Newtonsoft JToken objects to native .NET types.
        /// Required because Newtonsoft.Json deserializes nested objects as JObject/JArray, which
        /// System.Text.Json (used by SignalR) cannot serialize correctly - producing [[[]]] instead of real values.
        /// </summary>
        private static void NormalizeEventData(EnrollmentEvent evt)
        {
            if (evt.Data == null || evt.Data.Count == 0) return;
            var normalized = new Dictionary<string, object>();
            foreach (var kvp in evt.Data)
                normalized[kvp.Key] = ConvertJTokenToNative(kvp.Value);
            evt.Data = normalized;
        }

        private static object ConvertJTokenToNative(object value)
        {
            if (value is JArray jArray)
                return jArray.Select(item => ConvertJTokenToNative(item)).ToList<object>();
            if (value is JObject jObject)
            {
                var dict = new Dictionary<string, object>();
                foreach (var prop in jObject.Properties())
                    dict[prop.Name] = ConvertJTokenToNative(prop.Value);
                return dict;
            }
            if (value is JValue jValue)
                return jValue.Value ?? string.Empty;
            return value;
        }

        /// <summary>
        /// Aggregates app install events into AppInstallSummary records
        /// </summary>
        private void AggregateAppInstallEvent(EnrollmentEvent evt, string tenantId, string sessionId, Dictionary<string, AppInstallAggregationState> summaries)
        {
            // Agent sends: app_install_started, app_install_completed, app_install_failed, app_download_started, download_progress
            // Support both legacy (app_install_start/complete) and current agent event types
            bool isRelevant =
                evt.EventType == "app_install_started" || evt.EventType == "app_install_start" ||
                evt.EventType == "app_install_completed" || evt.EventType == "app_install_complete" ||
                evt.EventType == "app_install_failed" ||
                evt.EventType == "app_download_started" ||
                evt.EventType == "app_install_skipped" ||
                evt.EventType == "download_progress";

            if (!isRelevant) return;

            var appName = evt.Data?.ContainsKey("appName") == true ? evt.Data["appName"]?.ToString() : null;
            if (string.IsNullOrEmpty(appName)) return;

            if (!summaries.TryGetValue(appName, out var state))
            {
                state = new AppInstallAggregationState
                {
                    Summary = new AppInstallSummary
                    {
                        AppName = appName,
                        SessionId = sessionId,
                        TenantId = tenantId,
                        StartedAt = evt.Timestamp
                    }
                };
                summaries[appName] = state;
            }

            var summary = state.Summary;

            switch (evt.EventType)
            {
                case "app_install_started":
                case "app_install_start":
                    if (!state.InstallStartedAt.HasValue || evt.Timestamp < state.InstallStartedAt.Value)
                        state.InstallStartedAt = evt.Timestamp;
                    if (summary.Status == "InProgress" || summary.Status == string.Empty)
                        summary.Status = "InProgress";
                    break;

                case "app_download_started":
                    if (!state.DownloadStartedAt.HasValue || evt.Timestamp < state.DownloadStartedAt.Value)
                        state.DownloadStartedAt = evt.Timestamp;
                    if (summary.Status == "InProgress" || summary.Status == string.Empty)
                        summary.Status = "InProgress";
                    break;

                case "app_install_completed":
                case "app_install_complete":
                    summary.Status = "Succeeded";
                    summary.CompletedAt = evt.Timestamp;
                    if (summary.StartedAt != DateTime.MinValue)
                        summary.DurationSeconds = Math.Max(1, (int)(evt.Timestamp - summary.StartedAt).TotalSeconds);
                    break;

                case "app_install_failed":
                    summary.Status = "Failed";
                    summary.CompletedAt = evt.Timestamp;
                    if (summary.StartedAt != DateTime.MinValue)
                        summary.DurationSeconds = Math.Max(1, (int)(evt.Timestamp - summary.StartedAt).TotalSeconds);
                    // Agent does not send errorCode/errorMessage in Data — use the event message
                    summary.FailureCode = evt.Data?.ContainsKey("errorCode") == true
                        ? evt.Data["errorCode"]?.ToString() ?? string.Empty : string.Empty;
                    summary.FailureMessage = evt.Data?.ContainsKey("errorMessage") == true
                        ? evt.Data["errorMessage"]?.ToString() ?? string.Empty : evt.Message ?? string.Empty;
                    break;

                case "app_install_skipped":
                    // Mark as succeeded (skipped = already installed / not applicable) with 0 duration
                    if (summary.Status == "InProgress")
                        summary.Status = "Succeeded";
                    break;

                case "download_progress":
                    // Agent sends "bytesDownloaded" (not "bytes_downloaded")
                    var bytesKey = evt.Data?.ContainsKey("bytesDownloaded") == true ? "bytesDownloaded"
                        : evt.Data?.ContainsKey("bytes_downloaded") == true ? "bytes_downloaded" : null;
                    if (bytesKey != null && long.TryParse(evt.Data![bytesKey]?.ToString(), out var bytes))
                        summary.DownloadBytes = Math.Max(summary.DownloadBytes, bytes);
                    break;
            }

            RecalculateAppDurations(state);
        }

        private static void RecalculateAppDurations(AppInstallAggregationState state)
        {
            var summary = state.Summary;

            // Effective start for full app duration: earliest known install/download start.
            var effectiveStart = summary.StartedAt;
            if (state.DownloadStartedAt.HasValue &&
                (effectiveStart == DateTime.MinValue || state.DownloadStartedAt.Value < effectiveStart))
            {
                effectiveStart = state.DownloadStartedAt.Value;
            }

            if (state.InstallStartedAt.HasValue &&
                (effectiveStart == DateTime.MinValue || state.InstallStartedAt.Value < effectiveStart))
            {
                effectiveStart = state.InstallStartedAt.Value;
            }

            if (effectiveStart != DateTime.MinValue)
            {
                summary.StartedAt = effectiveStart;
            }

            // Download duration: from first download start to first install start.
            if (state.DownloadStartedAt.HasValue && state.InstallStartedAt.HasValue &&
                state.InstallStartedAt.Value >= state.DownloadStartedAt.Value)
            {
                summary.DownloadDurationSeconds = (int)(state.InstallStartedAt.Value - state.DownloadStartedAt.Value).TotalSeconds;
            }

            // Full duration: from effective start to completion/failure.
            if (summary.CompletedAt.HasValue && summary.StartedAt != DateTime.MinValue &&
                summary.CompletedAt.Value >= summary.StartedAt)
            {
                summary.DurationSeconds = (int)(summary.CompletedAt.Value - summary.StartedAt).TotalSeconds;
            }
        }

        private async Task<IngestEventsOutput> CreateErrorResponse(HttpRequestData req, HttpStatusCode statusCode, string message)
        {
            var response = req.CreateResponse(statusCode);
            var errorResponse = new IngestEventsResponse
            {
                Success = false,
                EventsReceived = 0,
                EventsProcessed = 0,
                Message = message,
                ProcessedAt = DateTime.UtcNow
            };
            await response.WriteAsJsonAsync(errorResponse);
            return new IngestEventsOutput { HttpResponse = response, SignalRMessages = Array.Empty<SignalRMessageAction>() };
        }
    }

    public class IngestEventsOutput
    {
        [HttpResult]
        public HttpResponseData? HttpResponse { get; set; }

        [SignalROutput(HubName = "autopilotmonitor")]
        public SignalRMessageAction[]? SignalRMessages { get; set; }
    }

    /// <summary>
    /// NDJSON metadata (first line of NDJSON payload)
    /// </summary>
    internal class NdjsonMetadata
    {
        public string SessionId { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
    }

    internal class AppInstallAggregationState
    {
        public AppInstallSummary Summary { get; set; } = new();
        public DateTime? DownloadStartedAt { get; set; }
        public DateTime? InstallStartedAt { get; set; }
    }
}
