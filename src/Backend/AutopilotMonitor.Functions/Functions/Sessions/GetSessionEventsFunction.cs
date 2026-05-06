using System.Net;
using System.Web;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Pagination;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Sessions
{
    public class GetSessionEventsFunction
    {
        private readonly ILogger<GetSessionEventsFunction> _logger;
        private readonly ISessionRepository _sessionRepo;

        public GetSessionEventsFunction(
            ILogger<GetSessionEventsFunction> logger,
            ISessionRepository sessionRepo)
        {
            _logger = logger;
            _sessionRepo = sessionRepo;
        }

        [Function("GetSessionEvents")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sessions/{sessionId}/events")] HttpRequestData req,
            string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequestResponse.WriteAsJsonAsync(new { success = false, message = "SessionId is required" });
                return badRequestResponse;
            }

            var sessionPrefix = $"[Session: {sessionId.Substring(0, Math.Min(8, sessionId.Length))}]";
            var query = HttpUtility.ParseQueryString(req.Url.Query ?? string.Empty);
            var pagination = SessionEventsPagination.ParseQuery(query);
            if (pagination.Error != null)
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteAsJsonAsync(new { success = false, message = pagination.Error });
                return bad;
            }

            _logger.LogInformation(
                "{Prefix} GetSessionEvents: Fetching events (pageSize={PageSize}, hasContinuation={HasContinuation})",
                sessionPrefix, pagination.PageSize?.ToString() ?? "all", pagination.Continuation != null);

            try
            {
                // Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware
                // Cross-tenant access check handled by middleware (TargetTenantId)
                var requestCtx = req.GetRequestContext();
                var tenantIdQueryParam = query["tenantId"];

                if (pagination.PageSize == null)
                {
                    // Legacy unpaginated path — full list, no nextLink.
                    var events = await _sessionRepo.GetSessionEventsAsync(requestCtx.TargetTenantId, sessionId);

                    if (events.Count == 0 && requestCtx.IsGlobalAdmin)
                    {
                        var resolvedTenantId = await _sessionRepo.FindSessionTenantIdAsync(sessionId);
                        if (resolvedTenantId != null && !string.Equals(resolvedTenantId, requestCtx.TargetTenantId, StringComparison.OrdinalIgnoreCase))
                        {
                            events = await _sessionRepo.GetSessionEventsAsync(resolvedTenantId, sessionId);
                        }
                    }

                    return await req.OkAsync(new
                    {
                        success = true,
                        sessionId,
                        count = events.Count,
                        events,
                    });
                }

                // Paginated path. Continuation token binds (tenantId, sessionId), so the
                // tenantId on every page must match the tenant the token was issued for.
                //
                // For Global Admin cross-tenant, the nextLink we emit echoes that tenant
                // back as ?tenantId=, so follow-up pages can re-bind to it. On page 1, if
                // GA hasn't passed tenantId yet, we resolve it from the session lookup.
                //
                // For non-GA, ?tenantId= in the URL is ignored — middleware-bound JWT
                // tenant is authoritative; we must never let a query param override it.
                var effectiveTenantId = requestCtx.TargetTenantId;

                if (requestCtx.IsGlobalAdmin)
                {
                    if (!string.IsNullOrEmpty(tenantIdQueryParam))
                    {
                        // Caller passed an explicit tenantId — including via the
                        // nextLink we emitted on a prior page. Cheap-path: trust it,
                        // skip the storage probe.
                        effectiveTenantId = tenantIdQueryParam;
                    }
                    else
                    {
                        // No tenantId in URL. Resolve it from the session lookup
                        // *on every page*, not just page 1, so external callers
                        // that strip nextLink down to the bare continuation token
                        // (older MCP clients, deployed agents) still validate.
                        // The lookup is one indexed point-read on SessionsIndex —
                        // negligible compared to the events page fetch itself.
                        var resolvedTenantId = await _sessionRepo.FindSessionTenantIdAsync(sessionId);
                        if (resolvedTenantId != null && !string.Equals(resolvedTenantId, effectiveTenantId, StringComparison.OrdinalIgnoreCase))
                        {
                            effectiveTenantId = resolvedTenantId;
                        }
                    }
                }

                string? azureToken = null;
                if (pagination.Continuation != null)
                {
                    if (!SessionEventsPagination.TryAcceptContinuation(
                            pagination.Continuation, effectiveTenantId, sessionId, out azureToken, out var reason))
                    {
                        _logger.LogWarning(
                            "{Prefix} GetSessionEvents: continuation rejected ({Reason})",
                            sessionPrefix, reason);
                        var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                        await bad.WriteAsJsonAsync(new
                        {
                            success = false,
                            message = $"Invalid continuation token ({reason}). Restart pagination from the first page.",
                        });
                        return bad;
                    }
                }

                var page = await _sessionRepo.GetSessionEventsPageAsync(
                    effectiveTenantId, sessionId, pagination.PageSize.Value, azureToken);

                string? nextLink = null;
                if (!string.IsNullOrEmpty(page.NextRawToken))
                {
                    var fp = SessionEventsPagination.Fingerprint(effectiveTenantId, sessionId);
                    var wireToken = ContinuationToken.Encode(page.NextRawToken!, effectiveTenantId, fp);
                    nextLink = SessionEventsPagination.BuildNextLink(
                        sessionId, pagination.PageSize.Value, wireToken, effectiveTenantId);
                }

                return await req.OkAsync(new
                {
                    success = true,
                    sessionId,
                    count = page.Items.Count,
                    events = page.Items,
                    nextLink,
                });
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, $"Get events for session '{sessionId}'");
            }
        }
    }
}
