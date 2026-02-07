using System.IO.Compression;
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

namespace AutopilotMonitor.Functions.Functions
{
    public class IngestEventsFunction
    {
        private readonly ILogger<IngestEventsFunction> _logger;
        private readonly TableStorageService _storageService;
        private readonly TenantConfigurationService _configService;
        private readonly RateLimitService _rateLimitService;
        private readonly AnalyzeRuleService _analyzeRuleService;

        public IngestEventsFunction(
            ILogger<IngestEventsFunction> logger,
            TableStorageService storageService,
            TenantConfigurationService configService,
            RateLimitService rateLimitService,
            AnalyzeRuleService analyzeRuleService)
        {
            _logger = logger;
            _storageService = storageService;
            _configService = configService;
            _rateLimitService = rateLimitService;
            _analyzeRuleService = analyzeRuleService;
        }

        [Function("IngestEvents")]
        public async Task<IngestEventsOutput> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "events/ingest")] HttpRequestData req)
        {
            try
            {
                // Parse request - support both NDJSON+gzip (new) and JSON (legacy)
                IngestEventsRequest? request = null;

                // Check Content-Encoding header for gzip
                var contentEncoding = req.Headers.Contains("Content-Encoding")
                    ? req.Headers.GetValues("Content-Encoding").FirstOrDefault()
                    : null;

                var contentType = req.Headers.Contains("Content-Type")
                    ? req.Headers.GetValues("Content-Type").FirstOrDefault()
                    : "application/json";

                if (contentEncoding == "gzip" && contentType?.Contains("application/x-ndjson") == true)
                {
                    // New format: NDJSON + gzip
                    _logger.LogDebug("Parsing gzip-compressed NDJSON request");

                    // Extract tenantId from header to get config (for payload size limit)
                    var tenantIdHeader = req.Headers.Contains("X-Tenant-Id")
                        ? req.Headers.GetValues("X-Tenant-Id").FirstOrDefault()
                        : null;

                    request = await ParseNdjsonGzipRequest(req.Body, tenantIdHeader);
                }
                else
                {
                    // Legacy format: standard JSON (backwards compatibility)
                    _logger.LogDebug("Parsing legacy JSON request");
                    if (req.Headers.TryGetValues("Content-Length", out var clValues)
                        && long.TryParse(clValues.FirstOrDefault(), out var contentLength)
                        && contentLength > 1_048_576) // 1 MB limit
                    {
                        return await CreateErrorResponse(req, HttpStatusCode.BadRequest, "Request body too large");
                    }
                    var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                    request = JsonConvert.DeserializeObject<IngestEventsRequest>(requestBody);
                }

                if (request?.Events == null || request.Events.Count == 0)
                {
                    return await CreateErrorResponse(req, HttpStatusCode.BadRequest, "No events provided");
                }

                if (string.IsNullOrEmpty(request.SessionId) || string.IsNullOrEmpty(request.TenantId))
                {
                    return await CreateErrorResponse(req, HttpStatusCode.BadRequest, "SessionId and TenantId are required");
                }

                // Validate request security (certificate, rate limit, hardware whitelist)
                var (validation, errorResponse) = await req.ValidateSecurityAsync(
                    request.TenantId,
                    _configService,
                    _rateLimitService,
                    _logger
                );

                if (errorResponse != null)
                {
                    // Security validation failed - return error response
                    return new IngestEventsOutput
                    {
                        HttpResponse = errorResponse,
                        SignalRMessages = Array.Empty<SignalRMessageAction>()
                    };
                }

                var sessionPrefix = $"[Session: {request.SessionId.Substring(0, Math.Min(8, request.SessionId.Length))}]";
                _logger.LogInformation($"{sessionPrefix} IngestEvents: Processing {request.Events.Count} events (Device: {validation.CertificateThumbprint}, Hardware: {validation.Manufacturer} {validation.Model}, Rate: {validation.RateLimitResult?.RequestsInWindow}/{validation.RateLimitResult?.MaxRequests})");

                // Store events in Azure Table Storage
                int processedCount = 0;
                var storedEvents = new List<EnrollmentEvent>();
                EnrollmentEvent? lastPhaseChangeEvent = null;
                EnrollmentEvent? completionEvent = null;
                EnrollmentEvent? failureEvent = null;

                // Track app install events for AppInstallSummary aggregation
                var appInstallUpdates = new Dictionary<string, AppInstallSummary>(StringComparer.OrdinalIgnoreCase);

                foreach (var evt in request.Events)
                {
                    var stored = await _storageService.StoreEventAsync(evt);
                    if (stored)
                    {
                        processedCount++;
                        storedEvents.Add(evt);
                        _logger.LogDebug($"Event: {evt.EventType} - {evt.Message}");

                        // Track special events for session status updates
                        if (evt.EventType == "phase_changed")
                        {
                            lastPhaseChangeEvent = evt;
                        }
                        else if (evt.EventType == "enrollment_complete")
                        {
                            completionEvent = evt;
                        }
                        else if (evt.EventType == "enrollment_failed")
                        {
                            failureEvent = evt;
                        }

                        // Track app install events for per-app metrics
                        AggregateAppInstallEvent(evt, request.TenantId, request.SessionId, appInstallUpdates);
                    }
                    else
                    {
                        _logger.LogWarning($"Failed to store event: {evt.EventType}");
                    }
                }

                // Store app install summaries
                foreach (var summary in appInstallUpdates.Values)
                {
                    await _storageService.StoreAppInstallSummaryAsync(summary);
                }

                // Update session status based on events
                if (completionEvent != null)
                {
                    // Enrollment completed successfully
                    await _storageService.UpdateSessionStatusAsync(
                        request.TenantId,
                        request.SessionId,
                        SessionStatus.Succeeded,
                        completionEvent.Phase
                    );
                    _logger.LogInformation($"{sessionPrefix} Status: Succeeded");
                }
                else if (failureEvent != null)
                {
                    // Enrollment failed
                    var failureReason = failureEvent.Data?.ContainsKey("errorCode") == true
                        ? $"{failureEvent.Message} ({failureEvent.Data["errorCode"]})"
                        : failureEvent.Message;

                    await _storageService.UpdateSessionStatusAsync(
                        request.TenantId,
                        request.SessionId,
                        SessionStatus.Failed,
                        failureEvent.Phase,
                        failureReason
                    );
                    _logger.LogWarning($"{sessionPrefix} Status: Failed - {failureReason}");
                }
                else if (lastPhaseChangeEvent != null)
                {
                    // Update current phase (still in progress)
                    await _storageService.UpdateSessionStatusAsync(
                        request.TenantId,
                        request.SessionId,
                        SessionStatus.InProgress,
                        lastPhaseChangeEvent.Phase
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
                _ = _storageService.IncrementPlatformStatAsync("TotalEventsProcessed", processedCount);
                if (newRuleResults.Count > 0)
                    _ = _storageService.IncrementPlatformStatAsync("IssuesDetected", newRuleResults.Count);
                if (completionEvent != null)
                    _ = _storageService.IncrementPlatformStatAsync("SuccessfulEnrollments");

                // Retrieve updated session data to include in SignalR messages
                var updatedSession = await _storageService.GetSessionAsync(request.TenantId, request.SessionId);

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

                var messagePayload = new {
                    sessionId = request.SessionId,
                    tenantId = request.TenantId,
                    eventCount = processedCount,
                    session = updatedSession, // Include full session data
                    newRuleResults = newRuleResults.Count > 0 ? newRuleResults : null
                };

                // 1. Summary notification for session list updates (tenant-specific only)
                // Only sent to clients in the tenant group - prevents cross-tenant data leaks
                // Galactic Admins only receive "newSession" events from RegisterSessionFunction,
                // not every event batch, to avoid flooding them with updates
                var summaryMessage = new SignalRMessageAction("newevents")
                {
                    GroupName = $"tenant-{request.TenantId}",
                    Arguments = new[] { messagePayload }
                };

                // 2. Detailed events for real-time event streaming on detail pages (session-specific)
                // Only sent to clients viewing this specific session - massive cost savings
                var eventsMessage = new SignalRMessageAction("eventStream")
                {
                    GroupName = $"session-{request.TenantId}-{request.SessionId}",
                    Arguments = new object[] { new {
                        sessionId = request.SessionId,
                        tenantId = request.TenantId,
                        events = storedEvents,
                        session = updatedSession, // Include full session data
                        newRuleResults = newRuleResults.Count > 0 ? newRuleResults : null
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
        /// Aggregates app install events into AppInstallSummary records
        /// </summary>
        private void AggregateAppInstallEvent(EnrollmentEvent evt, string tenantId, string sessionId, Dictionary<string, AppInstallSummary> summaries)
        {
            string? appName = null;

            if (evt.EventType == "app_install_start" || evt.EventType == "app_install_complete" ||
                evt.EventType == "app_install_failed" || evt.EventType == "download_progress")
            {
                appName = evt.Data?.ContainsKey("appName") == true ? evt.Data["appName"]?.ToString() : null;
                if (string.IsNullOrEmpty(appName)) return;
            }
            else
            {
                return;
            }

            if (!summaries.TryGetValue(appName, out var summary))
            {
                summary = new AppInstallSummary
                {
                    AppName = appName,
                    SessionId = sessionId,
                    TenantId = tenantId,
                    StartedAt = evt.Timestamp
                };
                summaries[appName] = summary;
            }

            switch (evt.EventType)
            {
                case "app_install_start":
                    summary.Status = "InProgress";
                    summary.StartedAt = evt.Timestamp;
                    break;

                case "app_install_complete":
                    summary.Status = "Succeeded";
                    summary.CompletedAt = evt.Timestamp;
                    if (summary.StartedAt != DateTime.MinValue)
                    {
                        summary.DurationSeconds = (int)(evt.Timestamp - summary.StartedAt).TotalSeconds;
                    }
                    break;

                case "app_install_failed":
                    summary.Status = "Failed";
                    summary.CompletedAt = evt.Timestamp;
                    if (summary.StartedAt != DateTime.MinValue)
                    {
                        summary.DurationSeconds = (int)(evt.Timestamp - summary.StartedAt).TotalSeconds;
                    }
                    summary.FailureCode = evt.Data?.ContainsKey("errorCode") == true
                        ? evt.Data["errorCode"]?.ToString() ?? string.Empty : string.Empty;
                    summary.FailureMessage = evt.Data?.ContainsKey("errorMessage") == true
                        ? evt.Data["errorMessage"]?.ToString() ?? string.Empty : evt.Message ?? string.Empty;
                    break;

                case "download_progress":
                    if (evt.Data?.ContainsKey("bytes_downloaded") == true &&
                        long.TryParse(evt.Data["bytes_downloaded"]?.ToString(), out var bytes))
                    {
                        summary.DownloadBytes = Math.Max(summary.DownloadBytes, bytes);
                    }
                    if (evt.Data?.ContainsKey("download_duration_seconds") == true &&
                        int.TryParse(evt.Data["download_duration_seconds"]?.ToString(), out var dlDuration))
                    {
                        summary.DownloadDurationSeconds = Math.Max(summary.DownloadDurationSeconds, dlDuration);
                    }
                    break;
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
}
