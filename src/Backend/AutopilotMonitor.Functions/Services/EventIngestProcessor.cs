using System.Linq;
using AutopilotMonitor.Functions.Functions.Ingest;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Functions.Services.Vulnerability;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Notifications;
using Microsoft.ApplicationInsights;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// M5.b.2 — Post-parse event-processing pipeline for the V2 <c>/api/agent/telemetry</c>
    /// endpoint. Deliberate <b>copy</b> of <see cref="IngestEventsFunction"/>'s post-body-parse
    /// tail, extracted into a standalone service so the new endpoint has identical event
    /// behaviour (rule engine, app-install aggregation, SignalR, vulnerability correlation,
    /// webhooks, SLA breach evaluation, AdminAction detection, ServerAction delivery) without
    /// touching the production-hot legacy path.
    /// <para>
    /// <b>Why a copy, not an extraction?</b> /api/agent/ingest serves live traffic from every
    /// deployed V1 agent. Refactoring it is a production risk we chose not to take. The
    /// duplicate disappears when the legacy endpoint is decommissioned post-v11 GA (see
    /// tasks/todo.md → Follow-Ups → Legacy-Agent-Ausmusterung). Until then: bugs fixed in
    /// legacy must be ported here manually.
    /// </para>
    /// <para>
    /// Pure static helpers (<see cref="IngestEventsFunction.StampServerFields"/>,
    /// <see cref="IngestEventsFunction.SanitizeEventTimestamps"/>) and the internal DTOs
    /// <c>EventClassification</c> / <c>AppInstallAggregationState</c> are <b>shared</b>
    /// (state-less, stable, no behaviour) — the duplication is scoped to the logic that
    /// actually runs.
    /// </para>
    /// </summary>
    public sealed class EventIngestProcessor
    {
        private readonly ILogger<EventIngestProcessor> _logger;
        private readonly ISessionRepository _sessionRepo;
        private readonly IMetricsRepository _metricsRepo;
        private readonly IRuleRepository _ruleRepo;
        private readonly IVulnerabilityRepository _vulnRepo;
        private readonly TenantConfigurationService _configService;
        private readonly AnalyzeRuleService _analyzeRuleService;
        private readonly WebhookNotificationService _webhookNotificationService;
        private readonly VulnerabilityCorrelationService _vulnerabilityCorrelation;
        private readonly AdminConfigurationService _adminConfigService;
        private readonly SignalRNotificationService _signalRNotification;
        private readonly OpsEventService _opsEventService;
        private readonly SlaBreachEvaluationService _slaBreachService;
        private readonly TelemetryClient _telemetryClient;

        public EventIngestProcessor(
            ILogger<EventIngestProcessor> logger,
            ISessionRepository sessionRepo,
            IMetricsRepository metricsRepo,
            IRuleRepository ruleRepo,
            IVulnerabilityRepository vulnRepo,
            TenantConfigurationService configService,
            AnalyzeRuleService analyzeRuleService,
            WebhookNotificationService webhookNotificationService,
            VulnerabilityCorrelationService vulnerabilityCorrelation,
            AdminConfigurationService adminConfigService,
            SignalRNotificationService signalRNotification,
            OpsEventService opsEventService,
            SlaBreachEvaluationService slaBreachService,
            TelemetryClient telemetryClient)
        {
            _logger = logger;
            _sessionRepo = sessionRepo;
            _metricsRepo = metricsRepo;
            _ruleRepo = ruleRepo;
            _vulnRepo = vulnRepo;
            _configService = configService;
            _analyzeRuleService = analyzeRuleService;
            _webhookNotificationService = webhookNotificationService;
            _vulnerabilityCorrelation = vulnerabilityCorrelation;
            _adminConfigService = adminConfigService;
            _signalRNotification = signalRNotification;
            _opsEventService = opsEventService;
            _slaBreachService = slaBreachService;
            _telemetryClient = telemetryClient;
        }

        /// <summary>
        /// Runs the full event-processing pipeline on an already-parsed batch. Mirror of
        /// <see cref="IngestEventsFunction"/>'s <c>ProcessIngestAsync</c> tail starting at
        /// timestamp sanitation (security checks, device/version kill-switches, NDJSON body
        /// parse and tenant-mismatch check are the caller's responsibility — the V2 function
        /// does them before it even knows the item is an Event).
        /// </summary>
        public async Task<EventIngestResult> ProcessEventsAsync(
            IngestEventsRequest request,
            SecurityValidationResult validation)
        {
            var sessionPrefix = $"[Session: {request.SessionId.Substring(0, Math.Min(8, request.SessionId.Length))}]";
            _logger.LogInformation(
                "{SessionPrefix} IngestTelemetry→EventProcessor: {Count} events (Device: {Cert}, Hardware: {Mfg} {Model}, Rate: {InWindow}/{MaxReq})",
                sessionPrefix, request.Events.Count,
                validation.CertificateThumbprint,
                validation.Manufacturer,
                validation.Model,
                validation.RateLimitResult?.RequestsInWindow,
                validation.RateLimitResult?.MaxRequests);

            var receivedAt = DateTime.UtcNow;
            IngestEventsFunction.StampServerFields(request.Events, request.TenantId, request.SessionId, receivedAt);
            IngestEventsFunction.SanitizeEventTimestamps(request.Events, receivedAt, _logger);

            var storedEvents = await _sessionRepo.StoreEventsBatchAsync(request.Events);
            int processedCount = storedEvents.Count;

            var indexTenantId = request.TenantId;
            var indexSessionId = request.SessionId;
            var indexEvents = storedEvents.ToList();
            _ = Task.WhenAll(
                _sessionRepo.UpsertEventTypeIndexBatchAsync(indexTenantId, indexSessionId, indexEvents),
                _sessionRepo.UpsertDeviceSnapshotAsync(indexTenantId, indexSessionId, indexEvents)
            ).ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                "Index update failed (non-fatal)"), TaskContinuationOptions.OnlyOnFaulted);

            var imeVersionEvent = request.Events.FirstOrDefault(e =>
                e.EventType == "ime_agent_version" && e.Data?.ContainsKey("agentVersion") == true);
            if (imeVersionEvent != null)
            {
                var imeVersion = imeVersionEvent.Data!["agentVersion"]?.ToString();
                if (!string.IsNullOrEmpty(imeVersion))
                {
                    _ = _sessionRepo.UpdateSessionImeAgentVersionAsync(request.TenantId, request.SessionId, imeVersion)
                        .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                            "ImeAgentVersion update failed (non-fatal)"), TaskContinuationOptions.OnlyOnFaulted);

                    _ = _sessionRepo.RecordImeVersionAsync(imeVersion, request.TenantId, request.SessionId)
                        .ContinueWith(async t =>
                        {
                            if (t.IsFaulted)
                            {
                                _logger.LogWarning(t.Exception?.InnerException,
                                    "ImeVersionHistory update failed (non-fatal)");
                            }
                            else if (t.Result)
                            {
                                await _opsEventService.RecordNewImeVersionDetectedAsync(
                                    imeVersion, request.TenantId, request.SessionId);
                            }
                        }, TaskScheduler.Default);
                }
            }

            var classification = ClassifyEvents(storedEvents);

            foreach (var summary in classification.AppInstallUpdates.Values)
            {
                await _metricsRepo.StoreAppInstallSummaryAsync(summary.Summary);
            }

            if (classification.DeviceLocationEvent?.Data != null)
            {
                var geoData = classification.DeviceLocationEvent.Data;
                var geoTenantId = request.TenantId;
                var geoSessionId = request.SessionId;
                _ = _sessionRepo.UpdateSessionGeoAsync(
                    geoTenantId,
                    geoSessionId,
                    geoData.ContainsKey("country") ? geoData["country"]?.ToString() : null,
                    geoData.ContainsKey("region") ? geoData["region"]?.ToString() : null,
                    geoData.ContainsKey("city") ? geoData["city"]?.ToString() : null,
                    geoData.ContainsKey("loc") ? geoData["loc"]?.ToString() : null
                ).ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                    "Fire-and-forget UpdateSessionGeoAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
            }

            var (statusTransitioned, whiteGloveStatusTransitioned, failureReason) =
                await UpdateSessionStatusAsync(request, sessionPrefix, classification);

            if (processedCount > 0)
            {
                await _sessionRepo.IncrementSessionEventCountAsync(
                    request.TenantId,
                    request.SessionId,
                    processedCount,
                    classification.EarliestEventTimestamp,
                    classification.LatestEventTimestamp,
                    currentPhase: classification.LastPhaseChangeEvent?.Phase,
                    platformScriptIncrement: classification.PlatformScriptCount,
                    remediationScriptIncrement: classification.RemediationScriptCount);
            }

            var newRuleResults = new List<RuleResult>();
            if (classification.CompletionEvent != null || classification.FailureEvent != null)
            {
                var ruleSessionId = request.SessionId;
                var ruleTenantId = request.TenantId;
                var rulePrefix = sessionPrefix;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var ruleEngine = new RuleEngine(_analyzeRuleService, _ruleRepo, _sessionRepo, _logger);
                        var outcome = await ruleEngine.AnalyzeSessionAsync(ruleTenantId, ruleSessionId);

                        foreach (var result in outcome.Results)
                        {
                            await _ruleRepo.StoreRuleResultAsync(result);
                        }

                        if (outcome.Results.Count > 0)
                        {
                            _logger.LogInformation(
                                "{Prefix} Enrollment-end analysis (async): {Count} issue(s) detected",
                                rulePrefix, outcome.Results.Count);

                            await _signalRNotification.NotifyRuleResultsAvailableAsync(
                                ruleTenantId, ruleSessionId, outcome.Results.Count);
                        }

                        if (outcome.Results.Count > 0)
                        {
                            _ = _metricsRepo.IncrementPlatformStatAsync("IssuesDetected", outcome.Results.Count)
                                .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                                    "Fire-and-forget IncrementPlatformStatAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
                        }

                        _ = RecordAnalyzeRuleStatsAsync(ruleTenantId, outcome);
                    }
                    catch (Exception ruleEx)
                    {
                        _logger.LogWarning(ruleEx, "{Prefix} Enrollment-end analysis failed (async, non-fatal)", rulePrefix);
                    }
                });
            }

            var shutdownInventoryDetected = storedEvents.Any(e =>
                e.EventType == Shared.Constants.EventTypes.SoftwareInventoryAnalysis &&
                e.Data != null &&
                e.Data.ContainsKey("triggered_at") &&
                e.Data["triggered_at"]?.ToString() == "shutdown" &&
                e.Data.ContainsKey("chunk_index") &&
                Convert.ToInt32(e.Data["chunk_index"]) == 0);

            if (shutdownInventoryDetected)
            {
                var capturedSessionId = request.SessionId;
                var capturedTenantId = request.TenantId;
                var capturedPrefix = sessionPrefix;

                var allInventoryItems = new List<Dictionary<string, object>>();
                var inventoryChunks = storedEvents
                    .Where(e => e.EventType == Shared.Constants.EventTypes.SoftwareInventoryAnalysis &&
                        e.Data != null &&
                        e.Data.ContainsKey("triggered_at") &&
                        e.Data["triggered_at"]?.ToString() == "shutdown" &&
                        e.Data.ContainsKey("inventory"))
                    .OrderBy(e => Convert.ToInt32(e.Data!.GetValueOrDefault("chunk_index", 0)))
                    .ToList();

                foreach (var chunk in inventoryChunks)
                {
                    if (chunk.Data!["inventory"] is System.Collections.IEnumerable items)
                    {
                        foreach (var item in items)
                        {
                            if (item is Dictionary<string, object> dict)
                                allInventoryItems.Add(dict);
                        }
                    }
                }

                int? whiteGlovePart = null;
                var firstShutdownChunk = inventoryChunks.FirstOrDefault();
                if (firstShutdownChunk?.Data != null &&
                    firstShutdownChunk.Data.TryGetValue("whiteglove_part", out var wgPartObj))
                {
                    whiteGlovePart = Convert.ToInt32(wgPartObj);
                }

                if (allInventoryItems.Count > 0)
                {
                    var capturedWhiteGlovePart = whiteGlovePart;

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var adminConfig = await _adminConfigService.GetConfigurationAsync();
                            if (adminConfig?.VulnerabilityCorrelationEnabled != true)
                                return;

                            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                            var reportData = await _vulnerabilityCorrelation.CorrelateAsync(
                                capturedSessionId, capturedTenantId, allInventoryItems, cts.Token);

                            if (reportData != null)
                            {
                                var phaseLabel = capturedWhiteGlovePart == 1 ? "device_setup"
                                    : capturedWhiteGlovePart == 2 ? "user_enrollment"
                                    : (string?)null;

                                if (phaseLabel != null && reportData.ContainsKey("findings")
                                    && reportData["findings"] is List<Dictionary<string, object>> tagFindings)
                                {
                                    foreach (var f in tagFindings)
                                        f.TryAdd("phase", phaseLabel);
                                }

                                if (capturedWhiteGlovePart == 2)
                                {
                                    try
                                    {
                                        var existingReport = await _vulnRepo.GetVulnerabilityReportAsync(
                                            capturedTenantId, capturedSessionId);
                                        if (existingReport != null)
                                        {
                                            reportData = VulnerabilityCorrelationService.MergeReports(
                                                existingReport, reportData,
                                                existingPhaseLabel: "device_setup",
                                                newPhaseLabel: "user_enrollment");
                                            _logger.LogInformation(
                                                "{Prefix} WhiteGlove Part 2: merged vulnerability report with Part 1 findings",
                                                capturedPrefix);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogWarning(ex,
                                            "{Prefix} Failed to load Part 1 report for merge (storing Part 2 standalone)",
                                            capturedPrefix);
                                    }
                                }

                                await _vulnRepo.StoreVulnerabilityReportAsync(
                                    capturedTenantId, capturedSessionId, reportData);
                                _logger.LogInformation(
                                    "{Prefix} Vulnerability correlation complete (async, whiteGlovePart={Part})",
                                    capturedPrefix, capturedWhiteGlovePart?.ToString() ?? "none");

                                var findings = reportData.ContainsKey("findings")
                                    ? reportData["findings"] as List<Dictionary<string, object>>
                                    : null;
                                if (findings != null && findings.Count > 0)
                                {
                                    _ = _sessionRepo.UpsertCveIndexEntriesAsync(capturedTenantId, capturedSessionId, findings)
                                        .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                                            "CveIndex update failed (non-fatal)"), TaskContinuationOptions.OnlyOnFaulted);
                                }

                                var overallRisk = reportData.ContainsKey("scan_summary")
                                    && reportData["scan_summary"] is Dictionary<string, object> summary
                                    && summary.ContainsKey("overall_risk")
                                    ? summary["overall_risk"]?.ToString() ?? "unknown"
                                    : "unknown";
                                await _signalRNotification.NotifyVulnerabilityReportAvailableAsync(
                                    capturedTenantId, capturedSessionId, overallRisk);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "{Prefix} Vulnerability correlation failed (async, non-fatal)", capturedPrefix);
                        }
                    });
                }
            }

            _ = _metricsRepo.IncrementPlatformStatAsync("TotalEventsProcessed", processedCount)
                .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                    "Fire-and-forget IncrementPlatformStatAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
            if (classification.CompletionEvent != null)
                _ = _metricsRepo.IncrementPlatformStatAsync("SuccessfulEnrollments")
                    .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                        "Fire-and-forget IncrementPlatformStatAsync failed"), TaskContinuationOptions.OnlyOnFaulted);

            _ = RecordGatherRuleStatsAsync(request.TenantId, storedEvents)
                .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                    "Fire-and-forget RecordGatherRuleStatsAsync failed"), TaskContinuationOptions.OnlyOnFaulted);

            if (classification.DiagnosticsUploadedEvent != null)
            {
                var blobName = classification.DiagnosticsUploadedEvent.Data?.ContainsKey("blobName") == true
                    ? classification.DiagnosticsUploadedEvent.Data["blobName"]?.ToString()
                    : null;
                if (!string.IsNullOrEmpty(blobName))
                {
                    await _sessionRepo.UpdateSessionDiagnosticsBlobAsync(
                        request.TenantId, request.SessionId, blobName);
                }
            }

            var updatedSession = await _sessionRepo.GetSessionAsync(request.TenantId, request.SessionId);

            if (updatedSession != null && updatedSession.Status == SessionStatus.InProgress)
            {
                var sessionAge = DateTime.UtcNow - updatedSession.StartedAt;
                if (sessionAge.TotalHours > 4)
                {
                    _logger.LogWarning(
                        "Session {SessionId} (tenant {TenantId}) still InProgress after {Hours:F1}h — may be stuck",
                        request.SessionId, request.TenantId, sessionAge.TotalHours);
                }
            }

            if (classification.WhiteGloveEvent != null && updatedSession?.IsPreProvisioned != true)
            {
                _logger.LogError(
                    "{SessionPrefix} WhiteGlove status update not persisted after retries and fallback. " +
                    "IsPreProvisioned={IsPreProvisioned}, Status={Status}. " +
                    "Proceeding with 200 to allow agent spool drain.",
                    sessionPrefix, updatedSession?.IsPreProvisioned, updatedSession?.Status);
            }

            await SendWebhookNotificationsAsync(
                request, sessionPrefix, classification, updatedSession,
                statusTransitioned, whiteGloveStatusTransitioned, failureReason, newRuleResults);

            if (statusTransitioned && updatedSession?.Status == SessionStatus.Failed)
            {
                _ = _slaBreachService.EvaluateSessionCompletionAsync(request.TenantId, updatedSession);
            }

            string? adminAction = null;
            if (updatedSession != null &&
                classification.CompletionEvent == null && classification.FailureEvent == null &&
                (updatedSession.Status == SessionStatus.Succeeded || updatedSession.Status == SessionStatus.Failed))
            {
                adminAction = updatedSession.Status.ToString();
                _logger.LogInformation(
                    "{SessionPrefix} Admin override detected — signaling agent: AdminAction={AdminAction}",
                    sessionPrefix, adminAction);
            }

            List<ServerAction>? pendingActions = null;
            if (updatedSession != null && !string.IsNullOrEmpty(updatedSession.PendingActionsJson))
            {
                var fetched = await _sessionRepo.FetchAndClearPendingActionsAsync(request.TenantId, request.SessionId);
                if (fetched.Count > 0)
                {
                    pendingActions = fetched;
                    foreach (var a in fetched)
                    {
                        _telemetryClient.TrackEvent("ServerActionDelivered", new Dictionary<string, string>
                        {
                            { "tenantId", request.TenantId },
                            { "sessionId", request.SessionId },
                            { "actionType", a.Type ?? string.Empty },
                            { "reason", a.Reason ?? string.Empty },
                            { "ruleId", a.RuleId ?? string.Empty },
                            { "queuedAt", a.QueuedAt.ToString("O") },
                            { "ageSeconds", ((int)(DateTime.UtcNow - a.QueuedAt).TotalSeconds).ToString() }
                        });
                    }
                    _logger.LogInformation(
                        "{SessionPrefix} Delivering {Count} server action(s): [{Types}]",
                        sessionPrefix, fetched.Count, string.Join(",", fetched.Select(a => a.Type)));
                }
            }

            var signalRMessages = BuildSignalRMessages(request, updatedSession, processedCount, newRuleResults);

            return new EventIngestResult
            {
                EventsProcessed = processedCount,
                AdminAction     = adminAction,
                PendingActions  = pendingActions,
                SignalRMessages = signalRMessages,
            };
        }

        // --------------------------------------------------------------------------- Helpers
        // Everything below is a verbatim copy of IngestEventsFunction's private instance helpers.
        // Keep the two copies in sync until /api/agent/ingest is decommissioned.

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
                    case "whiteglove_started":
                        classification.WhiteGloveStartedEvent = evt;
                        break;
                    case "esp_failure":
                        classification.EspFailureEvent = evt;
                        break;
                    case "device_location":
                        classification.DeviceLocationEvent = evt;
                        break;
                    case "session_stalled":
                        classification.SessionStalledEvent = evt;
                        break;
                    case "script_completed":
                    case "script_failed":
                        var scriptType = evt.Data?.ContainsKey("scriptType") == true
                            ? evt.Data["scriptType"]?.ToString() : null;
                        if (string.Equals(scriptType, "platform", StringComparison.OrdinalIgnoreCase))
                            classification.PlatformScriptCount++;
                        else if (string.Equals(scriptType, "remediation", StringComparison.OrdinalIgnoreCase))
                            classification.RemediationScriptCount++;
                        break;
                }

                if (!IsPeriodicOrStallEvent(evt.EventType))
                    classification.HasNonPeriodicRealEvent = true;

                AggregateAppInstallEvent(evt, storedEvents[0].TenantId!, storedEvents[0].SessionId!, classification.AppInstallUpdates);
            }

            return classification;
        }

        private static bool IsPeriodicOrStallEvent(string? eventType) => eventType switch
        {
            "performance_snapshot" => true,
            "agent_metrics_snapshot" => true,
            "performance_collector_stopped" => true,
            "agent_metrics_collector_stopped" => true,
            "stall_probe_check" => true,
            "stall_probe_result" => true,
            "session_stalled" => true,
            "modern_deployment_log" => true,
            _ => false
        };

        private async Task<(bool statusTransitioned, bool whiteGloveStatusTransitioned, string? failureReason)>
            UpdateSessionStatusAsync(IngestEventsRequest request, string sessionPrefix, EventClassification c)
        {
            bool statusTransitioned = false;
            bool whiteGloveStatusTransitioned = false;
            string? failureReason = null;

            if (c.CompletionEvent != null)
            {
                statusTransitioned = await _sessionRepo.UpdateSessionStatusAsync(
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

                statusTransitioned = await _sessionRepo.UpdateSessionStatusAsync(
                    request.TenantId, request.SessionId, SessionStatus.Failed, c.FailureEvent.Phase, failureReason,
                    completedAt: c.FailureEvent.Timestamp,
                    earliestEventTimestamp: c.EarliestEventTimestamp, latestEventTimestamp: c.LatestEventTimestamp);
                _logger.LogWarning("{SessionPrefix} Status: Failed - {FailureReason} (transitioned={Transitioned})", sessionPrefix, failureReason, statusTransitioned);
            }
            else if (c.EspFailureEvent != null)
            {
                failureReason = c.EspFailureEvent.Message ?? "ESP failure (backend fallback)";
                statusTransitioned = await _sessionRepo.UpdateSessionStatusAsync(
                    request.TenantId, request.SessionId, SessionStatus.Failed, c.EspFailureEvent.Phase, failureReason,
                    completedAt: c.EspFailureEvent.Timestamp,
                    earliestEventTimestamp: c.EarliestEventTimestamp, latestEventTimestamp: c.LatestEventTimestamp);
                _logger.LogWarning("{SessionPrefix} Status: Failed via esp_failure fallback - {FailureReason} (transitioned={Transitioned})",
                    sessionPrefix, failureReason, statusTransitioned);
            }
            else if (c.GatherCompletionEvent != null)
            {
                await _sessionRepo.UpdateSessionStatusAsync(
                    request.TenantId, request.SessionId, SessionStatus.Succeeded, c.GatherCompletionEvent.Phase,
                    completedAt: c.GatherCompletionEvent.Timestamp,
                    earliestEventTimestamp: c.EarliestEventTimestamp, latestEventTimestamp: c.LatestEventTimestamp);
                _logger.LogInformation("{SessionPrefix} Status: Succeeded (gather_rules)", sessionPrefix);
            }
            else if (c.WhiteGloveEvent != null)
            {
                whiteGloveStatusTransitioned = await _sessionRepo.UpdateSessionStatusAsync(
                    request.TenantId, request.SessionId, SessionStatus.Pending, EnrollmentPhase.AppsDevice,
                    earliestEventTimestamp: c.EarliestEventTimestamp, latestEventTimestamp: c.LatestEventTimestamp,
                    isPreProvisioned: true, isUserDriven: false);

                if (!whiteGloveStatusTransitioned)
                {
                    _logger.LogWarning("{SessionPrefix} WhiteGlove UpdateSessionStatusAsync failed, attempting unconditional fallback for IsPreProvisioned + Status", sessionPrefix);
                    try
                    {
                        await _sessionRepo.SetSessionPreProvisionedAsync(request.TenantId, request.SessionId, true, SessionStatus.Pending, isUserDriven: false);
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
                var currentSession = await _sessionRepo.GetSessionAsync(request.TenantId, request.SessionId);
                if (currentSession?.Status == SessionStatus.Pending)
                {
                    await _sessionRepo.UpdateSessionStatusAsync(
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

            if (c.WhiteGloveStartedEvent != null)
            {
                _logger.LogInformation("{SessionPrefix} whiteglove_started detected (soft signal — not setting IsPreProvisioned, awaiting whiteglove_complete)", sessionPrefix);
            }

            if (c.SessionStalledEvent != null)
            {
                var stalledReason = "Agent reported stall after 60min without progress (stall_probe)";
                var stalledTransitioned = await _sessionRepo.UpdateSessionStatusAsync(
                    request.TenantId, request.SessionId, SessionStatus.Stalled,
                    earliestEventTimestamp: c.EarliestEventTimestamp, latestEventTimestamp: c.LatestEventTimestamp,
                    stalledAt: c.SessionStalledEvent.Timestamp, failureReason: stalledReason);
                if (stalledTransitioned)
                    _logger.LogWarning("{SessionPrefix} Status: Stalled (agent-reported via session_stalled event)", sessionPrefix);
            }
            else if (c.HasNonPeriodicRealEvent && !statusTransitioned && !whiteGloveStatusTransitioned)
            {
                var currentSession = await _sessionRepo.GetSessionAsync(request.TenantId, request.SessionId);
                if (currentSession?.Status == SessionStatus.Stalled)
                {
                    var healed = await _sessionRepo.UpdateSessionStatusAsync(
                        request.TenantId, request.SessionId, SessionStatus.InProgress,
                        earliestEventTimestamp: c.EarliestEventTimestamp, latestEventTimestamp: c.LatestEventTimestamp,
                        clearStalledAt: true, clearFailureReason: true);
                    if (healed)
                        _logger.LogInformation("{SessionPrefix} Status: InProgress (healed from Stalled by new real event)", sessionPrefix);
                }
            }

            return (statusTransitioned, whiteGloveStatusTransitioned, failureReason);
        }

        private async Task SendWebhookNotificationsAsync(
            IngestEventsRequest request, string sessionPrefix, EventClassification c,
            SessionSummary? updatedSession, bool statusTransitioned, bool whiteGloveStatusTransitioned, string? failureReason,
            List<RuleResult> ruleResults)
        {
            var tenantConfig = await _configService.GetConfigurationAsync(request.TenantId);
            var (webhookUrl, providerTypeInt) = tenantConfig.GetEffectiveWebhookConfig();

            if (string.IsNullOrEmpty(webhookUrl) || providerTypeInt == 0)
                return;

            var providerType = (WebhookProviderType)providerTypeInt;
            var sessionUrl = updatedSession != null
                ? $"https://www.autopilotmonitor.com/session/{request.TenantId}/{request.SessionId}"
                : null;

            if (statusTransitioned && (c.CompletionEvent != null || c.FailureEvent != null))
            {
                var notifySuccess = c.CompletionEvent != null && tenantConfig.GetEffectiveNotifyOnSuccess();
                var notifyFailure = c.FailureEvent != null && tenantConfig.GetEffectiveNotifyOnFailure();
                if (notifySuccess || notifyFailure)
                {
                    var duration = updatedSession?.DurationSeconds != null
                        ? TimeSpan.FromSeconds(updatedSession.DurationSeconds.Value)
                        : (TimeSpan?)null;

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
                    NotificationAlertBuilder.AddRuleResultSections(alert, ruleResults);

                    _ = _webhookNotificationService.SendNotificationAsync(webhookUrl, providerType, alert)
                        .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                            "Fire-and-forget webhook notification failed"), TaskContinuationOptions.OnlyOnFaulted);
                }
            }

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
                NotificationAlertBuilder.AddRuleResultSections(alert, ruleResults);

                _ = _webhookNotificationService.SendNotificationAsync(webhookUrl, providerType, alert)
                    .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                        "Fire-and-forget webhook notification failed"), TaskContinuationOptions.OnlyOnFaulted);
            }

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
                NotificationAlertBuilder.AddRuleResultSections(alert, ruleResults);

                _ = _webhookNotificationService.SendNotificationAsync(webhookUrl, providerType, alert)
                    .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                        "Fire-and-forget webhook notification failed"), TaskContinuationOptions.OnlyOnFaulted);
            }
        }

        private SignalRMessageAction[] BuildSignalRMessages(
            IngestEventsRequest request, SessionSummary? updatedSession, int processedCount,
            List<RuleResult> newRuleResults)
        {
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

        private async Task RecordGatherRuleStatsAsync(string tenantId, List<EnrollmentEvent> events)
        {
            try
            {
                var today = DateTime.UtcNow.ToString("yyyy-MM-dd");

                var gatherEvents = events.Where(e =>
                    e.Data != null &&
                    e.Data.ContainsKey("ruleId") &&
                    e.Source == "GatherRuleExecutor").ToList();

                if (gatherEvents.Count == 0) return;

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var evt in gatherEvents)
                {
                    var ruleId = evt.Data!["ruleId"]?.ToString();
                    if (string.IsNullOrEmpty(ruleId) || !seen.Add(ruleId)) continue;

                    var ruleTitle = evt.Data.ContainsKey("ruleTitle") ? evt.Data["ruleTitle"]?.ToString() ?? "" : "";

                    await _metricsRepo.IncrementRuleStatAsync(
                        today, tenantId, ruleId, "gather",
                        ruleTitle, "", "",
                        fired: true, confidenceScore: null);

                    await _metricsRepo.IncrementRuleStatAsync(
                        today, "global", ruleId, "gather",
                        ruleTitle, "", "",
                        fired: true, confidenceScore: null);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to record gather rule stats (non-fatal)");
            }
        }

        private async Task RecordAnalyzeRuleStatsAsync(string tenantId, AnalysisOutcome outcome)
        {
            try
            {
                var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
                var firedRuleIds = new HashSet<string>(outcome.Results.Select(r => r.RuleId));

                foreach (var rule in outcome.EvaluatedRules)
                {
                    var fired = firedRuleIds.Contains(rule.RuleId);
                    int? confidence = null;
                    if (fired)
                    {
                        var result = outcome.Results.FirstOrDefault(r => r.RuleId == rule.RuleId);
                        confidence = result?.ConfidenceScore;
                    }

                    await _metricsRepo.IncrementRuleStatAsync(
                        today, tenantId, rule.RuleId, "analyze",
                        rule.Title, rule.Category, rule.Severity,
                        fired, confidence);

                    await _metricsRepo.IncrementRuleStatAsync(
                        today, "global", rule.RuleId, "analyze",
                        rule.Title, rule.Category, rule.Severity,
                        fired, confidence);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to record analyze rule stats (non-fatal)");
            }
        }

        private void AggregateAppInstallEvent(EnrollmentEvent evt, string tenantId, string sessionId, Dictionary<string, AppInstallAggregationState> summaries)
        {
            bool isRelevant =
                evt.EventType == "app_install_started" || evt.EventType == "app_install_start" ||
                evt.EventType == "app_install_completed" || evt.EventType == "app_install_complete" ||
                evt.EventType == "app_install_failed" ||
                evt.EventType == "app_download_started" ||
                evt.EventType == "app_install_skipped" ||
                evt.EventType == "download_progress" ||
                evt.EventType == "do_telemetry";

            if (!isRelevant) return;

            var appName = evt.Data?.ContainsKey("appName") == true ? evt.Data["appName"]?.ToString()?.Trim() : null;
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

            if (evt.Data != null)
            {
                if (evt.Data.TryGetValue("appVersion", out var appVersionObj))
                {
                    var appVersion = appVersionObj?.ToString();
                    if (!string.IsNullOrWhiteSpace(appVersion))
                        summary.AppVersion = appVersion.Trim();
                }
                if (evt.Data.TryGetValue("appType", out var appTypeObj))
                {
                    var appType = appTypeObj?.ToString();
                    if (!string.IsNullOrWhiteSpace(appType))
                        summary.AppType = appType.Trim();
                }
                if (evt.Data.TryGetValue("attemptNumber", out var attemptObj) &&
                    int.TryParse(attemptObj?.ToString(), out var attempt) && attempt > 0)
                {
                    summary.AttemptNumber = Math.Max(summary.AttemptNumber, attempt);
                }
                if (evt.Data.TryGetValue("installerPhase", out var phaseObj))
                {
                    var phase = phaseObj?.ToString();
                    if (!string.IsNullOrWhiteSpace(phase))
                        summary.InstallerPhase = phase.Trim();
                }
                if (evt.Data.TryGetValue("exitCode", out var exitCodeObj) &&
                    int.TryParse(exitCodeObj?.ToString(), out var exitCode))
                {
                    summary.ExitCode = exitCode;
                }
                if (evt.Data.TryGetValue("detectionResult", out var detectionObj))
                {
                    var detection = detectionObj?.ToString();
                    if (!string.IsNullOrWhiteSpace(detection))
                        summary.DetectionResult = detection.Trim();
                }
            }

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
                        summary.DurationSeconds = Math.Max(1, EventTimestampValidator.SafeDurationSeconds(summary.StartedAt, evt.Timestamp));
                    break;

                case "app_install_failed":
                    summary.Status = "Failed";
                    summary.CompletedAt = evt.Timestamp;
                    if (summary.StartedAt != DateTime.MinValue)
                        summary.DurationSeconds = Math.Max(1, EventTimestampValidator.SafeDurationSeconds(summary.StartedAt, evt.Timestamp));
                    summary.FailureCode = evt.Data?.ContainsKey("errorCode") == true
                        ? evt.Data["errorCode"]?.ToString() ?? string.Empty : string.Empty;
                    summary.FailureMessage = evt.Data?.ContainsKey("errorMessage") == true
                        ? evt.Data["errorMessage"]?.ToString() ?? string.Empty : evt.Message ?? string.Empty;
                    break;

                case "app_install_skipped":
                    if (summary.Status == "InProgress")
                        summary.Status = "Succeeded";
                    break;

                case "download_progress":
                    var bytesKey = evt.Data?.ContainsKey("bytesDownloaded") == true ? "bytesDownloaded"
                        : evt.Data?.ContainsKey("bytes_downloaded") == true ? "bytes_downloaded" : null;
                    if (bytesKey != null && long.TryParse(evt.Data![bytesKey]?.ToString(), out var bytes))
                        summary.DownloadBytes = Math.Max(summary.DownloadBytes, bytes);
                    break;

                case "do_telemetry":
                    if (evt.Data != null)
                    {
                        if (evt.Data.ContainsKey("doFileSize") && long.TryParse(evt.Data["doFileSize"]?.ToString(), out var doFs))
                        {
                            summary.DownloadBytes = Math.Max(summary.DownloadBytes, doFs);
                            summary.DoFileSize = doFs;
                        }
                        if (evt.Data.ContainsKey("doTotalBytesDownloaded") && long.TryParse(evt.Data["doTotalBytesDownloaded"]?.ToString(), out var doTotalDl))
                            summary.DoTotalBytesDownloaded = doTotalDl;
                        if (evt.Data.ContainsKey("doBytesFromPeers") && long.TryParse(evt.Data["doBytesFromPeers"]?.ToString(), out var doPeers))
                            summary.DoBytesFromPeers = doPeers;
                        if (evt.Data.ContainsKey("doBytesFromHttp") && long.TryParse(evt.Data["doBytesFromHttp"]?.ToString(), out var doHttp))
                            summary.DoBytesFromHttp = doHttp;
                        if (evt.Data.ContainsKey("doPercentPeerCaching") && int.TryParse(evt.Data["doPercentPeerCaching"]?.ToString(), out var doPct))
                            summary.DoPercentPeerCaching = doPct;
                        if (evt.Data.ContainsKey("doDownloadMode") && int.TryParse(evt.Data["doDownloadMode"]?.ToString(), out var doMode))
                            summary.DoDownloadMode = doMode;
                        if (evt.Data.ContainsKey("doDownloadDuration"))
                        {
                            var doDurStr = evt.Data["doDownloadDuration"]?.ToString() ?? string.Empty;
                            summary.DoDownloadDuration = doDurStr;
                            if (TimeSpan.TryParse(doDurStr, out var doDurTs) && doDurTs.TotalSeconds >= 1)
                                summary.DownloadDurationSeconds = Math.Max(summary.DownloadDurationSeconds, (int)doDurTs.TotalSeconds);
                        }
                        if (evt.Data.ContainsKey("doBytesFromLanPeers") && long.TryParse(evt.Data["doBytesFromLanPeers"]?.ToString(), out var doLan))
                            summary.DoBytesFromLanPeers = doLan;
                        if (evt.Data.ContainsKey("doBytesFromGroupPeers") && long.TryParse(evt.Data["doBytesFromGroupPeers"]?.ToString(), out var doGroup))
                            summary.DoBytesFromGroupPeers = doGroup;
                        if (evt.Data.ContainsKey("doBytesFromInternetPeers") && long.TryParse(evt.Data["doBytesFromInternetPeers"]?.ToString(), out var doInet))
                            summary.DoBytesFromInternetPeers = doInet;
                    }
                    break;
            }

            IngestEventsFunction.RecalculateAppDurations(state);
        }
    }

    /// <summary>
    /// Result shape returned by <see cref="EventIngestProcessor.ProcessEventsAsync"/>. Mirrors
    /// the control-signal fields the V2 agent's UploadResult parser reads from the 2xx body
    /// (Plan §M4.6.ε) plus the SignalR messages for the real-time UI push.
    /// </summary>
    public sealed class EventIngestResult
    {
        public int EventsProcessed { get; set; }
        public string? AdminAction { get; set; }
        public List<ServerAction>? PendingActions { get; set; }
        public SignalRMessageAction[] SignalRMessages { get; set; } = Array.Empty<SignalRMessageAction>();
    }
}
