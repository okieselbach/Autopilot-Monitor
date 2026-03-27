using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Sessions
{
    public class GetAllSessionsFunction
    {
        private readonly ILogger<GetAllSessionsFunction> _logger;
        private readonly ISessionRepository _sessionRepo;

        public GetAllSessionsFunction(
            ILogger<GetAllSessionsFunction> logger,
            ISessionRepository sessionRepo)
        {
            _logger = logger;
            _sessionRepo = sessionRepo;
        }

        [Function("GetAllSessions")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/sessions")] HttpRequestData req)
        {
            _logger.LogInformation("GetAllSessions function processing request (Global Admin Mode)");

            try
            {
                // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
                var userEmail = TenantHelper.GetUserIdentifier(req);
                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                var cursor = query["cursor"];
                var tenantIdFilter = query["tenantId"];

                SessionPage page;

                if (!string.IsNullOrEmpty(tenantIdFilter))
                {
                    if (!Guid.TryParse(tenantIdFilter, out _))
                    {
                        var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                        await badRequest.WriteAsJsonAsync(new { success = false, message = "Invalid tenantId format" });
                        return badRequest;
                    }

                    _logger.LogInformation("Fetching sessions for tenant {TenantId} (User: {UserEmail}, cursor: {Cursor})", tenantIdFilter, userEmail, cursor ?? "none");
                    page = await _sessionRepo.GetSessionsAsync(tenantIdFilter, maxResults: 100, cursor: cursor);
                }
                else
                {
                    _logger.LogInformation("Fetching all sessions across all tenants (User: {UserEmail}, cursor: {Cursor})", userEmail, cursor ?? "none");
                    page = await _sessionRepo.GetAllSessionsAsync(maxResults: 100, cursor: cursor);
                }

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    count = page.Sessions.Count,
                    hasMore = page.HasMore,
                    cursor = page.Cursor,
                    sessions = page.Sessions
                });

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting all sessions");

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
