using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Sessions
{
    /// <summary>
    /// <c>GET /api/sessions/{sessionId}/decision-graph</c> — returns a pre-projected DAG of
    /// the session's reducer steps for the Inspector (Plan §M5 / §M6). Building the graph
    /// backend-side means the UI receives one structured shape instead of rebuilding it from
    /// the raw journal on every page open; it also keeps the "what counts as terminal" rule
    /// authoritatively next to <see cref="DecisionGraphBuilder"/>'s copy of that list.
    /// </summary>
    public class GetSessionDecisionGraphFunction
    {
        /// <summary>Sane upper bound — a V2 session journal shouldn't produce more steps than this.</summary>
        internal const int MaxTransitionsToLoad = 5000;

        private readonly ILogger<GetSessionDecisionGraphFunction> _logger;
        private readonly IDecisionTransitionRepository _transitionRepo;
        private readonly ISessionRepository _sessionRepo;

        public GetSessionDecisionGraphFunction(
            ILogger<GetSessionDecisionGraphFunction> logger,
            IDecisionTransitionRepository transitionRepo,
            ISessionRepository sessionRepo)
        {
            _logger = logger;
            _transitionRepo = transitionRepo;
            _sessionRepo = sessionRepo;
        }

        [Function("GetSessionDecisionGraph")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sessions/{sessionId}/decision-graph")] HttpRequestData req,
            string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { success = false, message = "SessionId is required" });
                return badRequest;
            }

            var sessionPrefix = $"[Session: {sessionId.Substring(0, Math.Min(8, sessionId.Length))}]";
            _logger.LogInformation("{Prefix} GetSessionDecisionGraph", sessionPrefix);

            try
            {
                var requestCtx = req.GetRequestContext();

                var transitions = await _transitionRepo.QueryBySessionAsync(
                    requestCtx.TargetTenantId, sessionId, MaxTransitionsToLoad);

                // Global Admin cross-tenant fallback (same pattern as other session-detail reads).
                var effectiveTenantId = requestCtx.TargetTenantId;
                if (transitions.Count == 0 && requestCtx.IsGlobalAdmin)
                {
                    var resolvedTenantId = await _sessionRepo.FindSessionTenantIdAsync(sessionId);
                    if (resolvedTenantId != null &&
                        !string.Equals(resolvedTenantId, requestCtx.TargetTenantId, StringComparison.OrdinalIgnoreCase))
                    {
                        transitions = await _transitionRepo.QueryBySessionAsync(
                            resolvedTenantId, sessionId, MaxTransitionsToLoad);
                        effectiveTenantId = resolvedTenantId;
                    }
                }

                var projection = DecisionGraphBuilder.Build(effectiveTenantId, sessionId, transitions);

                return await req.OkAsync(new
                {
                    success = true,
                    truncated = transitions.Count >= MaxTransitionsToLoad,
                    graph = projection,
                });
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, $"Get decision graph for session '{sessionId}'");
            }
        }
    }
}
