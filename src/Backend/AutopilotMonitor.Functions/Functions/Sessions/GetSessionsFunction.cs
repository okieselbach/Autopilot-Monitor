using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Sessions
{
    public class GetSessionsFunction
    {
        private readonly ILogger<GetSessionsFunction> _logger;
        private readonly ISessionRepository _sessionRepo;

        public GetSessionsFunction(ILogger<GetSessionsFunction> logger, ISessionRepository sessionRepo)
        {
            _logger = logger;
            _sessionRepo = sessionRepo;
        }

        [Function("GetSessions")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sessions")] HttpRequestData req)
        {
            _logger.LogInformation("GetSessions function processing request");

            try
            {
                // Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware
                var tenantId = TenantHelper.GetTenantId(req);
                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                var cursor = query["cursor"];

                _logger.LogInformation("Fetching sessions for tenant {TenantId} (cursor: {Cursor})", tenantId, cursor ?? "none");

                var page = await _sessionRepo.GetSessionsAsync(tenantId, maxResults: 100, cursor: cursor);

                return await req.OkAsync(new
                {
                    success = true,
                    count = page.Sessions.Count,
                    hasMore = page.HasMore,
                    cursor = page.Cursor,
                    sessions = page.Sessions
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting sessions");

                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "Internal server error",
                    count = 0,
                    hasMore = false,
                    sessions = Array.Empty<object>()
                });

                return errorResponse;
            }
        }
    }
}
