using System.Linq;
using System.Net;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Notifications;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.SignalRService;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Ingest
{
    /// <summary>
    /// Partial: Core ingest processing logic — event classification, session updates,
    /// rule analysis, Teams notifications, and SignalR message construction.
    /// </summary>
    public partial class IngestEventsFunction
    {
        /// <summary>
        /// Core ingest logic: device block check, NDJSON parsing, event storage, rule engine, SignalR.
        /// Called by both the cert-auth Run() method and the bootstrap wrapper.
        /// </summary>
        internal async Task<IngestEventsOutput> ProcessIngestAsync(HttpRequestData req, string tenantId, SecurityValidationResult validation)
        {
                // --- Device block check (after security, before body decompression) ---
                // Check if this device has been administratively blocked (e.g. rogue device sending excessive data).
                // We read the serial number from the header (same header used by AutopilotDeviceValidator).
                // Using HTTP 200 with DeviceBlocked=true so the agent does not trigger its auth-failure circuit breaker.
                var serialNumberHeader = req.Headers.Contains("X-Device-SerialNumber")
                    ? req.Headers.GetValues("X-Device-SerialNumber").FirstOrDefault()
                    : null;

                if (!string.IsNullOrEmpty(serialNumberHeader))
                {
                    var (isBlocked, unblockAt, blockAction) = await _blockedDeviceService.IsBlockedAsync(tenantId, serialNumberHeader);
                    if (isBlocked)
                    {
                        var isKill = string.Equals(blockAction, "Kill", StringComparison.OrdinalIgnoreCase);

                        _logger.LogWarning(
                            "{Action} ingest from device: TenantId={TenantId}, SerialNumber={SerialNumber}, UnblockAt={UnblockAt}",
                            isKill ? "KILL signal for" : "Rejected", tenantId, serialNumberHeader, unblockAt);

                        var blockedHttpResponse = req.CreateResponse(System.Net.HttpStatusCode.OK);
                        await blockedHttpResponse.WriteAsJsonAsync(new IngestEventsResponse
                        {
                            Success = false,
                            DeviceBlocked = true,
                            DeviceKillSignal = isKill,
                            UnblockAt = unblockAt,
                            Message = isKill
                                ? "Device has been issued a remote kill signal."
                                : "Device is temporarily blocked by an administrator.",
                            ProcessedAt = DateTime.UtcNow
                        });
                        return new IngestEventsOutput
                        {
                            HttpResponse = blockedHttpResponse,
                            SignalRMessages = Array.Empty<SignalRMessageAction>()
                        };
                    }
                }

                // --- Version block check (global, applies to all tenants) ---
                var agentVersionHeader = req.Headers.Contains("X-Agent-Version")
                    ? req.Headers.GetValues("X-Agent-Version").FirstOrDefault()
                    : null;

                if (!string.IsNullOrEmpty(agentVersionHeader))
                {
                    var (isVersionBlocked, versionAction, matchedPattern) = await _blockedVersionService.IsVersionBlockedAsync(agentVersionHeader);
                    if (isVersionBlocked)
                    {
                        var isVersionKill = string.Equals(versionAction, "Kill", StringComparison.OrdinalIgnoreCase);

                        _logger.LogWarning(
                            "Version {Action} for agent: TenantId={TenantId}, AgentVersion={AgentVersion}, MatchedPattern={Pattern}",
                            isVersionKill ? "KILL" : "BLOCK", tenantId, agentVersionHeader, matchedPattern);

                        var versionBlockedResponse = req.CreateResponse(System.Net.HttpStatusCode.OK);
                        await versionBlockedResponse.WriteAsJsonAsync(new IngestEventsResponse
                        {
                            Success = false,
                            DeviceBlocked = true,
                            DeviceKillSignal = isVersionKill,
                            Message = isVersionKill
                                ? $"Agent version {agentVersionHeader} has been issued a remote kill signal (pattern: {matchedPattern})."
                                : $"Agent version {agentVersionHeader} is blocked by administrator (pattern: {matchedPattern}).",
                            ProcessedAt = DateTime.UtcNow
                        });
                        return new IngestEventsOutput
                        {
                            HttpResponse = versionBlockedResponse,
                            SignalRMessages = Array.Empty<SignalRMessageAction>()
                        };
                    }
                }

                // --- Parse NDJSON+gzip request body (only after security is cleared) ---
                var request = await ParseNdjsonGzipRequest(req.Body, tenantId);

                if (request?.Events == null || request.Events.Count == 0)
                {
                    return await CreateErrorResponse(req, HttpStatusCode.BadRequest, "No events provided");
                }

                if (string.IsNullOrEmpty(request.SessionId) || string.IsNullOrEmpty(request.TenantId))
                {
                    return await CreateErrorResponse(req, HttpStatusCode.BadRequest, "SessionId and TenantId are required");
                }

                // Ensure body TenantId matches the validated header TenantId (prevent body spoofing)
                if (!string.Equals(request.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("TenantId mismatch: header={HeaderTenantId}, body={BodyTenantId}", tenantId, request.TenantId);
                    return await CreateErrorResponse(req, HttpStatusCode.Forbidden, "TenantId mismatch between header and payload");
                }

                var sessionPrefix = $"[Session: {request.SessionId.Substring(0, Math.Min(8, request.SessionId.Length))}]";
                _logger.LogInformation($"{sessionPrefix} IngestEvents: Processing {request.Events.Count} events (Device: {validation.CertificateThumbprint}, Hardware: {validation.Manufacturer} {validation.Model}, Rate: {validation.RateLimitResult?.RequestsInWindow}/{validation.RateLimitResult?.MaxRequests})");

                // Store events in Azure Table Storage (batch write for efficiency)
                var storedEvents = await _storageService.StoreEventsBatchAsync(request.Events);
                int processedCount = storedEvents.Count;

                // Classify events for downstream processing
                var classification = ClassifyEvents(storedEvents);

                // Store app install summaries
                foreach (var summary in classification.AppInstallUpdates.Values)
                {
                    await _storageService.StoreAppInstallSummaryAsync(summary.Summary);
                }

                // Extract geo-location data and merge into session row
                if (classification.DeviceLocationEvent?.Data != null)
                {
                    var geoData = classification.DeviceLocationEvent.Data;
                    await _storageService.UpdateSessionGeoAsync(
                        request.TenantId,
                        request.SessionId,
                        geoData.ContainsKey("country") ? geoData["country"]?.ToString() : null,
                        geoData.ContainsKey("region") ? geoData["region"]?.ToString() : null,
                        geoData.ContainsKey("city") ? geoData["city"]?.ToString() : null,
                        geoData.ContainsKey("loc") ? geoData["loc"]?.ToString() : null
                    );
                }

                // Update session status based on events
                var (statusTransitioned, whiteGloveStatusTransitioned, failureReason) = await UpdateSessionStatusAsync(
                    request, sessionPrefix, classification);

                // Always increment event count when events were stored
                if (processedCount > 0)
                {
                    await _storageService.IncrementSessionEventCountAsync(
                        request.TenantId,
                        request.SessionId,
                        processedCount,
                        classification.EarliestEventTimestamp,
                        classification.LatestEventTimestamp,
                        currentPhase: classification.LastPhaseChangeEvent?.Phase
                    );
                }

                // Run full analysis when enrollment ends (cost-efficient: one pass over all events)
                var newRuleResults = new List<AutopilotMonitor.Shared.Models.RuleResult>();
                if (classification.CompletionEvent != null || classification.FailureEvent != null)
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
                if (classification.CompletionEvent != null)
                    _ = _storageService.IncrementPlatformStatAsync("SuccessfulEnrollments")
                        .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException, "Fire-and-forget IncrementPlatformStatAsync failed"), TaskContinuationOptions.OnlyOnFaulted);

                // Store diagnostics blob name on session (if agent uploaded a diagnostics package)
                if (classification.DiagnosticsUploadedEvent != null)
                {
                    var blobName = classification.DiagnosticsUploadedEvent.Data?.ContainsKey("blobName") == true
                        ? classification.DiagnosticsUploadedEvent.Data["blobName"]?.ToString()
                        : null;
                    if (!string.IsNullOrEmpty(blobName))
                    {
                        await _storageService.UpdateSessionDiagnosticsBlobAsync(
                            request.TenantId, request.SessionId, blobName);
                    }
                }

                // Retrieve updated session data to include in SignalR messages
                var updatedSession = await _storageService.GetSessionAsync(request.TenantId, request.SessionId);

                // Session age warning: log if session >4h old and still InProgress (observability only)
                if (updatedSession != null && updatedSession.Status == SessionStatus.InProgress)
                {
                    var sessionAge = DateTime.UtcNow - updatedSession.StartedAt;
                    if (sessionAge.TotalHours > 4)
                    {
                        _logger.LogWarning("Session {SessionId} (tenant {TenantId}) still InProgress after {Hours:F1}h — may be stuck",
                            request.SessionId, request.TenantId, sessionAge.TotalHours);
                    }
                }

                // Log warning if WhiteGlove status update was not persisted despite retries and fallback.
                if (classification.WhiteGloveEvent != null && updatedSession?.IsPreProvisioned != true)
                {
                    _logger.LogError(
                        "{SessionPrefix} WhiteGlove status update not persisted after retries and fallback. " +
                        "IsPreProvisioned={IsPreProvisioned}, Status={Status}. " +
                        "Proceeding with 200 to allow agent spool drain.",
                        sessionPrefix, updatedSession?.IsPreProvisioned, updatedSession?.Status);
                }

                // Send webhook notifications (fire-and-forget, non-fatal)
                await SendWebhookNotificationsAsync(
                    request, sessionPrefix, classification, updatedSession,
                    statusTransitioned, whiteGloveStatusTransitioned, failureReason);

                // Build HTTP response + SignalR messages
                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new IngestEventsResponse
                {
                    Success = true,
                    EventsReceived = request.Events.Count,
                    EventsProcessed = processedCount,
                    Message = $"Successfully stored {processedCount} of {request.Events.Count} events",
                    ProcessedAt = DateTime.UtcNow
                });

                var signalRMessages = BuildSignalRMessages(request, updatedSession, processedCount, newRuleResults);

                return new IngestEventsOutput
                {
                    HttpResponse = response,
                    SignalRMessages = signalRMessages
                };
        }

        /// <summary>
        /// Classifies stored events by type for downstream processing.
        /// </summary>
        private EventClassification ClassifyEvents(List<EnrollmentEvent> storedEvents)
        {
            var classification = new EventClassification();

            foreach (var evt in storedEvents)
            {
                if (!classification.EarliestEventTimestamp.HasValue || evt.Timestamp < classification.EarliestEventTimestamp.Value)
                    classification.EarliestEventTimestamp = evt.Timestamp;
                if (!classification.LatestEventTimestamp.HasValue || evt.Timestamp > classification.LatestEventTimestamp.Value)
                    classification.LatestEventTimestamp = evt.Timestamp;

                switch (evt.EventType)
                {
                    case "phase_changed":
                    case "esp_phase_changed":
                        classification.LastPhaseChangeEvent = evt;
                        break;
                    case "enrollment_complete":
                        classification.CompletionEvent = evt;
                        break;
                    case "gather_rules_collection_completed":
                        classification.GatherCompletionEvent = evt;
                        break;
                    case "enrollment_failed":
                        classification.FailureEvent = evt;
                        break;
                    case "diagnostics_uploaded":
                        classification.DiagnosticsUploadedEvent = evt;
                        break;
                    case "whiteglove_complete":
                        classification.WhiteGloveEvent = evt;
                        break;
                    case "whiteglove_resumed":
                        classification.WhiteGloveResumedEvent = evt;
                        break;
                    case "esp_failure":
                        classification.EspFailureEvent = evt;
                        break;
                    case "device_location":
                        classification.DeviceLocationEvent = evt;
                        break;
                }

                AggregateAppInstallEvent(evt, storedEvents[0].TenantId, storedEvents[0].SessionId, classification.AppInstallUpdates);
            }

            return classification;
        }

        /// <summary>
        /// Updates session status based on classified events.
        /// Returns (statusTransitioned, whiteGloveStatusTransitioned, failureReason).
        /// </summary>
        private async Task<(bool statusTransitioned, bool whiteGloveStatusTransitioned, string? failureReason)> UpdateSessionStatusAsync(
            IngestEventsRequest request, string sessionPrefix, EventClassification c)
        {
            bool statusTransitioned = false;
            bool whiteGloveStatusTransitioned = false;
            string? failureReason = null;

            if (c.CompletionEvent != null)
            {
                statusTransitioned = await _storageService.UpdateSessionStatusAsync(
                    request.TenantId, request.SessionId, SessionStatus.Succeeded, c.CompletionEvent.Phase,
                    completedAt: c.CompletionEvent.Timestamp,
                    earliestEventTimestamp: c.EarliestEventTimestamp, latestEventTimestamp: c.LatestEventTimestamp);
                _logger.LogInformation("{SessionPrefix} Status: Succeeded (transitioned={Transitioned})", sessionPrefix, statusTransitioned);
            }
            else if (c.FailureEvent != null)
            {
                failureReason = c.FailureEvent.Data?.ContainsKey("errorCode") == true
                    ? $"{c.FailureEvent.Message} ({c.FailureEvent.Data["errorCode"]})"
                    : c.FailureEvent.Message;

                statusTransitioned = await _storageService.UpdateSessionStatusAsync(
                    request.TenantId, request.SessionId, SessionStatus.Failed, c.FailureEvent.Phase, failureReason,
                    completedAt: c.FailureEvent.Timestamp,
                    earliestEventTimestamp: c.EarliestEventTimestamp, latestEventTimestamp: c.LatestEventTimestamp);
                _logger.LogWarning("{SessionPrefix} Status: Failed - {FailureReason} (transitioned={Transitioned})", sessionPrefix, failureReason, statusTransitioned);
            }
            else if (c.EspFailureEvent != null)
            {
                failureReason = c.EspFailureEvent.Message ?? "ESP failure (backend fallback)";
                statusTransitioned = await _storageService.UpdateSessionStatusAsync(
                    request.TenantId, request.SessionId, SessionStatus.Failed, c.EspFailureEvent.Phase, failureReason,
                    completedAt: c.EspFailureEvent.Timestamp,
                    earliestEventTimestamp: c.EarliestEventTimestamp, latestEventTimestamp: c.LatestEventTimestamp);
                _logger.LogWarning("{SessionPrefix} Status: Failed via esp_failure fallback - {FailureReason} (transitioned={Transitioned})",
                    sessionPrefix, failureReason, statusTransitioned);
            }
            else if (c.GatherCompletionEvent != null)
            {
                await _storageService.UpdateSessionStatusAsync(
                    request.TenantId, request.SessionId, SessionStatus.Succeeded, c.GatherCompletionEvent.Phase,
                    completedAt: c.GatherCompletionEvent.Timestamp,
                    earliestEventTimestamp: c.EarliestEventTimestamp, latestEventTimestamp: c.LatestEventTimestamp);
                _logger.LogInformation("{SessionPrefix} Status: Succeeded (gather_rules)", sessionPrefix);
            }
            else if (c.WhiteGloveEvent != null)
            {
                whiteGloveStatusTransitioned = await _storageService.UpdateSessionStatusAsync(
                    request.TenantId, request.SessionId, SessionStatus.Pending, c.WhiteGloveEvent.Phase,
                    earliestEventTimestamp: c.EarliestEventTimestamp, latestEventTimestamp: c.LatestEventTimestamp,
                    isPreProvisioned: true, isUserDriven: false);

                if (!whiteGloveStatusTransitioned)
                {
                    _logger.LogWarning("{SessionPrefix} WhiteGlove UpdateSessionStatusAsync failed, attempting unconditional fallback for IsPreProvisioned + Status", sessionPrefix);
                    try
                    {
                        await _storageService.SetSessionPreProvisionedAsync(request.TenantId, request.SessionId, true, SessionStatus.Pending, isUserDriven: false);
                        whiteGloveStatusTransitioned = true;
                        _logger.LogInformation("{SessionPrefix} WhiteGlove fallback succeeded: IsPreProvisioned + Status=Pending set via unconditional merge", sessionPrefix);
                    }
                    catch (Exception fallbackEx)
                    {
                        _logger.LogError(fallbackEx, "{SessionPrefix} WhiteGlove fallback SetSessionPreProvisionedAsync also failed", sessionPrefix);
                    }
                }

                _logger.LogInformation("{SessionPrefix} Status: Pending (WhiteGlove pre-provisioning complete, transitioned={Transitioned})", sessionPrefix, whiteGloveStatusTransitioned);
            }
            else if (c.WhiteGloveResumedEvent != null)
            {
                var currentSession = await _storageService.GetSessionAsync(request.TenantId, request.SessionId);
                if (currentSession?.Status == SessionStatus.Pending)
                {
                    await _storageService.UpdateSessionStatusAsync(
                        request.TenantId, request.SessionId, SessionStatus.InProgress, c.WhiteGloveResumedEvent.Phase,
                        earliestEventTimestamp: c.EarliestEventTimestamp, latestEventTimestamp: c.LatestEventTimestamp,
                        isUserDriven: true, resumedAt: c.WhiteGloveResumedEvent.Timestamp);
                    _logger.LogInformation("{SessionPrefix} Status: InProgress (WhiteGlove Part 2 resumed, IsUserDriven=true)", sessionPrefix);
                }
                else
                {
                    _logger.LogInformation("{SessionPrefix} WhiteGlove resumed skipped, session already {Status}", sessionPrefix, currentSession?.Status);
                }
            }

            return (statusTransitioned, whiteGloveStatusTransitioned, failureReason);
        }

        /// <summary>
        /// Sends webhook notifications for enrollment completion, WhiteGlove, and ESP failure events.
        /// Uses the channel-agnostic notification system with provider-specific renderers.
        /// </summary>
        private async Task SendWebhookNotificationsAsync(
            IngestEventsRequest request, string sessionPrefix, EventClassification c,
            SessionSummary? updatedSession, bool statusTransitioned, bool whiteGloveStatusTransitioned, string? failureReason)
        {
            // Read config once (was 3 separate reads before)
            var tenantConfig = await _configService.GetConfigurationAsync(request.TenantId);
            var (webhookUrl, providerTypeInt) = tenantConfig.GetEffectiveWebhookConfig();

            if (string.IsNullOrEmpty(webhookUrl) || providerTypeInt == 0)
                return;

            var providerType = (WebhookProviderType)providerTypeInt;
            var sessionUrl = updatedSession != null
                ? $"https://www.autopilotmonitor.com/session/{request.TenantId}/{request.SessionId}"
                : null;

            // Enrollment completion/failure notification
            // Only send when statusTransitioned=true to prevent duplicates on retry/double-upload
            if (statusTransitioned && (c.CompletionEvent != null || c.FailureEvent != null))
            {
                var notifySuccess = c.CompletionEvent != null && tenantConfig.GetEffectiveNotifyOnSuccess();
                var notifyFailure = c.FailureEvent != null && tenantConfig.GetEffectiveNotifyOnFailure();
                if (notifySuccess || notifyFailure)
                {
                    var duration = updatedSession?.DurationSeconds != null
                        ? TimeSpan.FromSeconds(updatedSession.DurationSeconds.Value)
                        : (TimeSpan?)null;

                    // For WhiteGlove sessions: show user enrollment duration only (Duration 2)
                    if (updatedSession?.IsPreProvisioned == true && updatedSession?.ResumedAt != null)
                    {
                        var completionTime = c.CompletionEvent?.Timestamp ?? c.FailureEvent?.Timestamp;
                        if (completionTime.HasValue)
                            duration = completionTime.Value - updatedSession.ResumedAt.Value;
                    }

                    var alert = NotificationAlertBuilder.BuildEnrollmentAlert(
                        updatedSession?.DeviceName,
                        updatedSession?.SerialNumber,
                        updatedSession?.Manufacturer,
                        updatedSession?.Model,
                        success: c.CompletionEvent != null,
                        failureReason: failureReason,
                        duration: duration,
                        sessionUrl: sessionUrl);

                    _ = _webhookNotificationService.SendNotificationAsync(webhookUrl, providerType, alert);
                }
            }

            // WhiteGlove pre-provisioning completion
            if (whiteGloveStatusTransitioned && c.WhiteGloveEvent != null && tenantConfig.GetEffectiveNotifyOnSuccess())
            {
                var duration = updatedSession?.DurationSeconds != null
                    ? TimeSpan.FromSeconds(updatedSession.DurationSeconds.Value)
                    : (TimeSpan?)null;

                var alert = NotificationAlertBuilder.BuildWhiteGloveAlert(
                    updatedSession?.DeviceName,
                    updatedSession?.SerialNumber,
                    updatedSession?.Manufacturer,
                    updatedSession?.Model,
                    success: true,
                    duration: duration,
                    sessionUrl: sessionUrl);

                _ = _webhookNotificationService.SendNotificationAsync(webhookUrl, providerType, alert);
            }

            // WhiteGlove pre-provisioning failure via esp_failure
            if (c.EspFailureEvent != null && updatedSession?.IsPreProvisioned == true && tenantConfig.GetEffectiveNotifyOnFailure())
            {
                var duration = updatedSession?.DurationSeconds != null
                    ? TimeSpan.FromSeconds(updatedSession.DurationSeconds.Value)
                    : (TimeSpan?)null;

                var alert = NotificationAlertBuilder.BuildWhiteGloveAlert(
                    updatedSession?.DeviceName,
                    updatedSession?.SerialNumber,
                    updatedSession?.Manufacturer,
                    updatedSession?.Model,
                    success: false,
                    duration: duration,
                    sessionUrl: sessionUrl);

                _ = _webhookNotificationService.SendNotificationAsync(webhookUrl, providerType, alert);
            }
        }

        /// <summary>
        /// Builds SignalR messages for tenant-level and session-level real-time updates.
        /// </summary>
        private SignalRMessageAction[] BuildSignalRMessages(
            IngestEventsRequest request, SessionSummary? updatedSession, int processedCount,
            List<AutopilotMonitor.Shared.Models.RuleResult> newRuleResults)
        {
            // 1. Summary notification for session list updates (tenant-specific only)
            // Send only mutable fields as delta update
            object? sessionDelta = updatedSession != null ? new {
                updatedSession.CurrentPhase,
                updatedSession.CurrentPhaseDetail,
                updatedSession.Status,
                updatedSession.FailureReason,
                updatedSession.EventCount,
                updatedSession.DurationSeconds,
                updatedSession.CompletedAt,
                updatedSession.DiagnosticsBlobName,
                updatedSession.IsPreProvisioned
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
                    newRuleResults = slimRuleResults
                } }
            };

            return new[] { summaryMessage, eventsMessage };
        }
    }

    /// <summary>
    /// Holds classified events from an ingest batch for downstream processing.
    /// </summary>
    internal class EventClassification
    {
        public EnrollmentEvent? LastPhaseChangeEvent { get; set; }
        public EnrollmentEvent? CompletionEvent { get; set; }
        public EnrollmentEvent? FailureEvent { get; set; }
        public EnrollmentEvent? GatherCompletionEvent { get; set; }
        public EnrollmentEvent? DiagnosticsUploadedEvent { get; set; }
        public EnrollmentEvent? WhiteGloveEvent { get; set; }
        public EnrollmentEvent? WhiteGloveResumedEvent { get; set; }
        public EnrollmentEvent? EspFailureEvent { get; set; }
        public EnrollmentEvent? DeviceLocationEvent { get; set; }
        public DateTime? EarliestEventTimestamp { get; set; }
        public DateTime? LatestEventTimestamp { get; set; }
        public Dictionary<string, AppInstallAggregationState> AppInstallUpdates { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
