using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Raw
{
    public class QueryRawEventsFunction
    {
        private readonly ILogger<QueryRawEventsFunction> _logger;
        private readonly ISessionRepository _sessionRepo;

        public QueryRawEventsFunction(ILogger<QueryRawEventsFunction> logger, ISessionRepository sessionRepo)
        {
            _logger = logger;
            _sessionRepo = sessionRepo;
        }

        /// <summary>
        /// GET /api/raw/events — Tenant-scoped raw event query (cross-session)
        /// </summary>
        [Function("QueryRawEvents")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "raw/events")] HttpRequestData req)
        {
            try
            {
                var tenantId = TenantHelper.GetTenantId(req);
                return await QueryEvents(req, tenantId);
            }
            catch (UnauthorizedAccessException)
            {
                var err = req.CreateResponse(HttpStatusCode.Unauthorized);
                await err.WriteAsJsonAsync(new { error = "Unauthorized" });
                return err;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "Query raw events");
            }
        }

        /// <summary>
        /// GET /api/global/raw/events — Cross-tenant raw event query (GlobalAdminOnly)
        /// Omit tenantId to query across all tenants.
        /// </summary>
        [Function("QueryRawEventsGlobal")]
        public async Task<HttpResponseData> RunGlobal(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/raw/events")] HttpRequestData req)
        {
            try
            {
                var tenantId = req.Query["tenantId"];
                return await QueryEvents(req, string.IsNullOrEmpty(tenantId) ? null : tenantId);
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "Query global raw events");
            }
        }

        private async Task<HttpResponseData> QueryEvents(HttpRequestData req, string? tenantId)
        {
            var sessionId = req.Query["sessionId"];
            var eventType = req.Query["eventType"];
            var severity = req.Query["severity"];
            var source = req.Query["source"];
            var startedAfter = req.Query["startedAfter"];
            var startedBefore = req.Query["startedBefore"];
            var limitStr = req.Query["limit"];
            var limit = int.TryParse(limitStr, out var l) ? Math.Clamp(l, 1, 500) : 100;

            List<EnrollmentEvent> events;

            if (!string.IsNullOrEmpty(sessionId))
            {
                if (string.IsNullOrEmpty(tenantId))
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteAsJsonAsync(new { error = "tenantId is required when querying by sessionId" });
                    return bad;
                }
                // Single session query — use existing repo method
                events = await _sessionRepo.GetSessionEventsAsync(tenantId, sessionId, limit);
            }
            else
            {
                // Cross-session: We need to search by event type via the EventTypeIndex
                // then fetch events for matched sessions
                if (string.IsNullOrEmpty(eventType))
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteAsJsonAsync(new { error = "Either sessionId or eventType is required for raw event queries" });
                    return bad;
                }

                // Use search by event to find sessions, then fetch their events
                var sessions = await _sessionRepo.SearchSessionsByEventAsync(tenantId, eventType, source, severity, null, limit: 20);
                events = new List<EnrollmentEvent>();
                foreach (var session in sessions)
                {
                    var sessionEvents = await _sessionRepo.GetSessionEventsAsync(session.TenantId, session.SessionId, 200);
                    events.AddRange(sessionEvents.Where(e =>
                        (string.IsNullOrEmpty(eventType) || e.EventType == eventType) &&
                        (string.IsNullOrEmpty(severity) || e.Severity.ToString().Equals(severity, StringComparison.OrdinalIgnoreCase)) &&
                        (string.IsNullOrEmpty(source) || (e.Source ?? "").Contains(source, StringComparison.OrdinalIgnoreCase))
                    ));

                    if (events.Count >= limit) break;
                }
                events = events.Take(limit).ToList();
            }

            // Apply client-side filters for single-session queries
            if (!string.IsNullOrEmpty(sessionId))
            {
                if (!string.IsNullOrEmpty(eventType))
                    events = events.Where(e => e.EventType == eventType).ToList();
                if (!string.IsNullOrEmpty(severity))
                    events = events.Where(e => e.Severity.ToString().Equals(severity, StringComparison.OrdinalIgnoreCase)).ToList();
                if (!string.IsNullOrEmpty(source))
                    events = events.Where(e => (e.Source ?? "").Contains(source, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            if (!string.IsNullOrEmpty(startedAfter) && DateTime.TryParse(startedAfter, out var after))
                events = events.Where(e => e.Timestamp >= after).ToList();
            if (!string.IsNullOrEmpty(startedBefore) && DateTime.TryParse(startedBefore, out var before))
                events = events.Where(e => e.Timestamp <= before).ToList();

            events = events.Take(limit).OrderBy(e => e.Timestamp).ThenBy(e => e.Sequence).ToList();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                tenantId,
                count = events.Count,
                events
            });
            return response;
        }
    }
}
