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

        public IngestEventsFunction(
            ILogger<IngestEventsFunction> logger,
            TableStorageService storageService,
            TenantConfigurationService configService,
            RateLimitService rateLimitService)
        {
            _logger = logger;
            _storageService = storageService;
            _configService = configService;
            _rateLimitService = rateLimitService;
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
                    request = await ParseNdjsonGzipRequest(req.Body);
                }
                else
                {
                    // Legacy format: standard JSON (backwards compatibility)
                    _logger.LogDebug("Parsing legacy JSON request");
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
                    }
                    else
                    {
                        _logger.LogWarning($"Failed to store event: {evt.EventType}");
                    }
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

                // 1. Summary notification for session list updates (tenant-specific)
                // Only sent to clients in the tenant group - prevents cross-tenant data leaks
                var summaryMessage = new SignalRMessageAction("newevents")
                {
                    GroupName = $"tenant-{request.TenantId}",
                    Arguments = new[] { new {
                        sessionId = request.SessionId,
                        tenantId = request.TenantId,
                        eventCount = processedCount,
                        session = updatedSession // Include full session data
                    } }
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
                        session = updatedSession // Include full session data
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
        private async Task<IngestEventsRequest> ParseNdjsonGzipRequest(Stream body)
        {
            // Decompress gzip
            using var decompressed = new MemoryStream();
            using (var gzip = new GZipStream(body, CompressionMode.Decompress, leaveOpen: true))
            {
                await gzip.CopyToAsync(decompressed);
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
