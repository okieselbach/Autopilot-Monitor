using System.Linq;
using AutopilotMonitor.Functions.Functions.Ingest;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Functions.Services.Vulnerability;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
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
    /// <para>
    /// Split across partials for readability — this file owns the orchestrator (ctor + DI +
    /// <see cref="ProcessEventsAsync"/>); thematic helpers live in siblings:
    /// <c>.Classification.cs</c> (<c>ClassifyEvents</c>, <c>IsPeriodicOrStallEvent</c>,
    /// <c>UpdateSessionStatusAsync</c>), <c>.Notifications.cs</c>
    /// (<c>SendWebhookNotificationsAsync</c>, <c>BuildSignalRMessages</c>),
    /// <c>.RuleStats.cs</c> (<c>RecordGatherRuleStatsAsync</c>,
    /// <c>RecordAnalyzeRuleStatsAsync</c>), <c>.AppInstall.cs</c>
    /// (<c>AggregateAppInstallEvent</c>).
    /// </para>
    /// </summary>
    public sealed partial class EventIngestProcessor
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
