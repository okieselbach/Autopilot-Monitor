using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Sessions
{
    public class GetSessionsFunction
    {
        private readonly ILogger<GetSessionsFunction> _logger;
        private readonly TableStorageService _storageService;

        public GetSessionsFunction(ILogger<GetSessionsFunction> logger, TableStorageService storageService)
        {
            _logger = logger;
            _storageService = storageService;
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

                var page = await _storageService.GetSessionsAsync(tenantId, maxResults: 100, cursor: cursor);

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
