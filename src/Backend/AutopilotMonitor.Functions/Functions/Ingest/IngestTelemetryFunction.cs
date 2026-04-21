using System.IO;
using System.Net;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Functions.Ingest
{
    /// <summary>
    /// V2 ingest endpoint. Consumes a heterogeneous batch of <see cref="TelemetryItemDto"/>s
    /// (Events + Signals + DecisionTransitions in a single JSON array, gzip-compressed on
    /// the wire — the <c>UseRequestDecompression</c> middleware decompresses before we
    /// parse). Plan §2.7a / §M5 / M4.6.ε.
    /// <para>
    /// <b>Routing by <see cref="TelemetryItemDto.Kind"/>:</b>
    /// <list type="bullet">
    ///   <item><c>Event</c> → <see cref="ISessionRepository.StoreEventsBatchAsync"/> (storage only in this build — full event pipeline sharing with /api/agent/ingest is tracked as M5.b.2).</item>
    ///   <item><c>Signal</c> → <see cref="ISignalRepository.StoreBatchAsync"/></item>
    ///   <item><c>DecisionTransition</c> → <see cref="IDecisionTransitionRepository.StoreBatchAsync"/></item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Response (M4.6.ε):</b> the agent parses <c>DeviceBlocked</c>/<c>UnblockAt</c>/
    /// <c>DeviceKillSignal</c>/<c>AdminAction</c>/<c>Actions</c> from the 2xx body and routes
    /// kill-switches through its <c>ServerActionDispatcher</c>. Populated from the same
    /// services the legacy /api/agent/ingest endpoint uses so behaviour is at parity.
    /// </para>
    /// </summary>
    public sealed class IngestTelemetryFunction
    {
        private readonly ILogger<IngestTelemetryFunction> _logger;
        private readonly ISessionRepository _sessionRepo;
        private readonly ISignalRepository _signalRepo;
        private readonly IDecisionTransitionRepository _transitionRepo;
        private readonly TenantConfigurationService _configService;
        private readonly RateLimitService _rateLimitService;
        private readonly AutopilotDeviceValidator _autopilotDeviceValidator;
        private readonly CorporateIdentifierValidator _corporateIdentifierValidator;
        private readonly DeviceAssociationValidator _deviceAssociationValidator;
        private readonly BootstrapSessionService _bootstrapSessionService;
        private readonly BlockedDeviceService _blockedDeviceService;
        private readonly BlockedVersionService _blockedVersionService;

        public IngestTelemetryFunction(
            ILogger<IngestTelemetryFunction> logger,
            ISessionRepository sessionRepo,
            ISignalRepository signalRepo,
            IDecisionTransitionRepository transitionRepo,
            TenantConfigurationService configService,
            RateLimitService rateLimitService,
            AutopilotDeviceValidator autopilotDeviceValidator,
            CorporateIdentifierValidator corporateIdentifierValidator,
            DeviceAssociationValidator deviceAssociationValidator,
            BootstrapSessionService bootstrapSessionService,
            BlockedDeviceService blockedDeviceService,
            BlockedVersionService blockedVersionService)
        {
            _logger = logger;
            _sessionRepo = sessionRepo;
            _signalRepo = signalRepo;
            _transitionRepo = transitionRepo;
            _configService = configService;
            _rateLimitService = rateLimitService;
            _autopilotDeviceValidator = autopilotDeviceValidator;
            _corporateIdentifierValidator = corporateIdentifierValidator;
            _deviceAssociationValidator = deviceAssociationValidator;
            _bootstrapSessionService = bootstrapSessionService;
            _blockedDeviceService = blockedDeviceService;
            _blockedVersionService = blockedVersionService;
        }

        [Function("IngestTelemetry")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "agent/telemetry")] HttpRequestData req)
        {
            try
            {
                var tenantIdHeader = req.Headers.Contains("X-Tenant-Id")
                    ? req.Headers.GetValues("X-Tenant-Id").FirstOrDefault()
                    : null;

                if (string.IsNullOrEmpty(tenantIdHeader))
                {
                    return await WriteErrorAsync(req, HttpStatusCode.BadRequest, "X-Tenant-Id header is required");
                }

                var (validation, errorResponse) = await req.ValidateSecurityAsync(
                    tenantIdHeader,
                    _configService,
                    _rateLimitService,
                    _autopilotDeviceValidator,
                    _corporateIdentifierValidator,
                    _logger,
                    bootstrapSessionService: _bootstrapSessionService,
                    deviceAssociationValidator: _deviceAssociationValidator);

                if (errorResponse != null) return errorResponse;

                // Device + version kill-switches: short-circuit before parsing the body.
                var serialNumberHeader = req.Headers.Contains("X-Device-SerialNumber")
                    ? req.Headers.GetValues("X-Device-SerialNumber").FirstOrDefault()
                    : null;
                var agentVersionHeader = req.Headers.Contains("X-Agent-Version")
                    ? req.Headers.GetValues("X-Agent-Version").FirstOrDefault()
                    : null;

                var killResponse = await CheckKillSwitchesAsync(
                    req, tenantIdHeader, serialNumberHeader, agentVersionHeader);
                if (killResponse != null) return killResponse;

                // Parse request body (already gzip-decompressed by middleware if Content-Encoding: gzip).
                List<TelemetryItemDto>? items;
                try
                {
                    using var reader = new StreamReader(req.Body);
                    var body = await reader.ReadToEndAsync();
                    items = JsonConvert.DeserializeObject<List<TelemetryItemDto>>(body);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "IngestTelemetry: malformed JSON body");
                    return await WriteErrorAsync(req, HttpStatusCode.BadRequest, "Malformed JSON body");
                }

                if (items == null || items.Count == 0)
                {
                    return await WriteErrorAsync(req, HttpStatusCode.BadRequest, "No telemetry items provided");
                }

                // Extract tenant+session from the first item's PartitionKey for body-vs-header tenant check
                // and for AdminAction / ServerAction lookups. All items in a batch belong to one session.
                if (!TryParsePartitionKey(items[0].PartitionKey, out var bodyTenantId, out var sessionId))
                {
                    return await WriteErrorAsync(req, HttpStatusCode.BadRequest, "Malformed PartitionKey");
                }

                if (!string.Equals(bodyTenantId, tenantIdHeader, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "IngestTelemetry: TenantId mismatch — header={Header}, body={Body}",
                        tenantIdHeader, bodyTenantId);
                    return await WriteErrorAsync(req, HttpStatusCode.Forbidden, "TenantId mismatch between header and payload");
                }

                // Partition by Kind and persist.
                var (eventCount, signalCount, transitionCount, unknownCount) =
                    await PersistItemsAsync(items, bodyTenantId, sessionId);

                _logger.LogInformation(
                    "IngestTelemetry: tenant={Tenant} session={Session} events={E} signals={S} transitions={T} unknown={U}",
                    bodyTenantId, sessionId, eventCount, signalCount, transitionCount, unknownCount);

                // Populate server→agent control signals (same services as /api/agent/ingest for parity).
                var adminAction = await TryReadAdminActionAsync(bodyTenantId, sessionId);
                var pendingActions = await _sessionRepo.FetchAndClearPendingActionsAsync(bodyTenantId, sessionId);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new IngestEventsResponse
                {
                    Success = true,
                    EventsReceived = items.Count,
                    EventsProcessed = eventCount + signalCount + transitionCount,
                    Message = $"Stored {eventCount} events, {signalCount} signals, {transitionCount} transitions",
                    ProcessedAt = DateTime.UtcNow,
                    AdminAction = adminAction,
                    Actions = pendingActions.Count > 0 ? pendingActions : null,
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "IngestTelemetry: unhandled exception");
                return await WriteErrorAsync(req, HttpStatusCode.InternalServerError, "Internal server error");
            }
        }

        /// <summary>
        /// Runs device-serial and agent-version kill-switch checks. Returns a 200 response with
        /// <c>DeviceBlocked=true</c> (and optional <c>DeviceKillSignal=true</c>) if the caller
        /// should stop — or null if the request may proceed.
        /// </summary>
        private async Task<HttpResponseData?> CheckKillSwitchesAsync(
            HttpRequestData req, string tenantId, string? serialNumber, string? agentVersion)
        {
            if (!string.IsNullOrEmpty(serialNumber))
            {
                // Session-aware block: without body-parse we can't discriminate on SessionId,
                // so we use the tenant/serial blanket check. Session-scoped blocks still require
                // the agent to upload; the response just carries DeviceBlocked=true regardless of
                // session — tighter scoping lands once the body is parsed (M5.b.2 pipeline share).
                var (isBlocked, unblockAt, blockAction, _) =
                    await _blockedDeviceService.IsBlockedAsync(tenantId, serialNumber);
                if (isBlocked)
                {
                    var isKill = string.Equals(blockAction, "Kill", StringComparison.OrdinalIgnoreCase);
                    _logger.LogWarning(
                        "IngestTelemetry: {Action} device tenant={Tenant} serial={Serial} unblockAt={UnblockAt}",
                        isKill ? "KILL" : "Block", tenantId, serialNumber, unblockAt);
                    return await WriteDeviceBlockedAsync(req, isKill, unblockAt,
                        isKill ? "Device has been issued a remote kill signal."
                               : "Device is temporarily blocked by an administrator.");
                }
            }

            if (!string.IsNullOrEmpty(agentVersion))
            {
                var (isVersionBlocked, versionAction, matchedPattern) =
                    await _blockedVersionService.IsVersionBlockedAsync(agentVersion);
                if (isVersionBlocked)
                {
                    var isKill = string.Equals(versionAction, "Kill", StringComparison.OrdinalIgnoreCase);
                    _logger.LogWarning(
                        "IngestTelemetry: version {Action} tenant={Tenant} agentVersion={AgentVersion} pattern={Pattern}",
                        isKill ? "KILL" : "block", tenantId, agentVersion, matchedPattern);
                    return await WriteDeviceBlockedAsync(req, isKill, null,
                        isKill ? $"Agent version {agentVersion} has been issued a remote kill signal (pattern: {matchedPattern})."
                               : $"Agent version {agentVersion} is blocked by administrator (pattern: {matchedPattern}).");
                }
            }

            return null;
        }

        private async Task<HttpResponseData> WriteDeviceBlockedAsync(
            HttpRequestData req, bool isKill, DateTime? unblockAt, string message)
        {
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new IngestEventsResponse
            {
                Success = false,
                DeviceBlocked = true,
                DeviceKillSignal = isKill,
                UnblockAt = unblockAt,
                Message = message,
                ProcessedAt = DateTime.UtcNow,
            });
            return response;
        }

        private async Task<string?> TryReadAdminActionAsync(string tenantId, string sessionId)
        {
            var session = await _sessionRepo.GetSessionAsync(tenantId, sessionId);
            if (session == null) return null;
            if (session.Status == SessionStatus.Succeeded) return "Succeeded";
            if (session.Status == SessionStatus.Failed)    return "Failed";
            return null;
        }

        private async Task<(int eventCount, int signalCount, int transitionCount, int unknownCount)>
            PersistItemsAsync(IReadOnlyList<TelemetryItemDto> items, string tenantId, string sessionId)
        {
            var events      = new List<EnrollmentEvent>();
            var signals     = new List<SignalRecord>();
            var transitions = new List<DecisionTransitionRecord>();
            var unknown     = 0;

            foreach (var item in items)
            {
                switch (item.Kind)
                {
                    case "Event":
                        var evt = TelemetryPayloadParser.ParseEvent(item, tenantId, sessionId);
                        if (evt != null) events.Add(evt);
                        break;
                    case "Signal":
                        var sig = TelemetryPayloadParser.ParseSignal(item, tenantId, sessionId);
                        if (sig != null) signals.Add(sig);
                        break;
                    case "DecisionTransition":
                        var tr = TelemetryPayloadParser.ParseTransition(item, tenantId, sessionId);
                        if (tr != null) transitions.Add(tr);
                        break;
                    default:
                        unknown++;
                        _logger.LogWarning("IngestTelemetry: unknown Kind '{Kind}' (TelemetryItemId={Id})", item.Kind, item.TelemetryItemId);
                        break;
                }
            }

            // Stamp authoritative TenantId/SessionId + ReceivedAt on events (same policy as legacy — never trust per-event agent values).
            var receivedAt = DateTime.UtcNow;
            foreach (var e in events)
            {
                e.TenantId = tenantId;
                e.SessionId = sessionId;
                e.ReceivedAt = receivedAt;
            }

            var eventCount = 0;
            if (events.Count > 0)
            {
                var stored = await _sessionRepo.StoreEventsBatchAsync(events);
                eventCount = stored.Count;
                // NOTE: rule engine, app-install aggregation, SignalR notification, vulnerability
                // correlation NOT invoked here. Legacy /api/agent/ingest still owns that pipeline —
                // sharing lands in M5.b.2 via an extracted EventIngestProcessor service.
            }

            var signalCount     = await _signalRepo.StoreBatchAsync(signals);
            var transitionCount = await _transitionRepo.StoreBatchAsync(transitions);

            return (eventCount, signalCount, transitionCount, unknown);
        }

        /// <summary>
        /// PartitionKey convention is <c>{tenantId}_{sessionId}</c> — both GUIDs with dashes, so
        /// splitting on the single underscore between them is unambiguous. Returns false for any
        /// shape that doesn't match (malformed, extra parts, empty halves).
        /// </summary>
        internal static bool TryParsePartitionKey(string partitionKey, out string tenantId, out string sessionId)
        {
            tenantId = string.Empty;
            sessionId = string.Empty;
            if (string.IsNullOrEmpty(partitionKey)) return false;

            var parts = partitionKey.Split('_');
            if (parts.Length != 2) return false;
            if (string.IsNullOrEmpty(parts[0]) || string.IsNullOrEmpty(parts[1])) return false;

            tenantId = parts[0];
            sessionId = parts[1];
            return true;
        }

        private static async Task<HttpResponseData> WriteErrorAsync(
            HttpRequestData req, HttpStatusCode status, string message)
        {
            var response = req.CreateResponse(status);
            await response.WriteAsJsonAsync(new IngestEventsResponse
            {
                Success = false,
                EventsReceived = 0,
                EventsProcessed = 0,
                Message = message,
                ProcessedAt = DateTime.UtcNow,
            });
            return response;
        }
    }
}
