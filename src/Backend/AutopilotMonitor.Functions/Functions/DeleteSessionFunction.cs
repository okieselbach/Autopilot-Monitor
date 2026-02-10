using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions
{
    public class DeleteSessionFunction
    {
        private readonly ILogger<DeleteSessionFunction> _logger;
        private readonly TableStorageService _storageService;

        public DeleteSessionFunction(ILogger<DeleteSessionFunction> logger, TableStorageService storageService)
        {
            _logger = logger;
            _storageService = storageService;
        }

        [Function("DeleteSession")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "sessions/{sessionId}")] HttpRequestData req,
            string sessionId)
        {
            _logger.LogInformation($"DeleteSession function processing request for session {sessionId}");

            try
            {
                // Validate authentication
                if (!TenantHelper.IsAuthenticated(req))
                {
                    _logger.LogWarning($"Unauthenticated DeleteSession attempt for session {sessionId}");
                    var unauthorizedResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                    await unauthorizedResponse.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Authentication required. Please provide a valid JWT token."
                    });
                    return unauthorizedResponse;
                }

                var tenantId = TenantHelper.GetTenantId(req);
                var userIdentifier = TenantHelper.GetUserIdentifier(req);

                _logger.LogInformation($"Deleting session {sessionId} for tenant {tenantId} by user {userIdentifier}");

                // Delete all related data for this session
                var eventsDeleted = await _storageService.DeleteSessionEventsAsync(tenantId, sessionId);
                _logger.LogInformation($"Deleted {eventsDeleted} events for session {sessionId}");

                var ruleResultsDeleted = await _storageService.DeleteSessionRuleResultsAsync(tenantId, sessionId);
                _logger.LogInformation($"Deleted {ruleResultsDeleted} rule results for session {sessionId}");

                var appSummariesDeleted = await _storageService.DeleteSessionAppInstallSummariesAsync(tenantId, sessionId);
                _logger.LogInformation($"Deleted {appSummariesDeleted} app install summaries for session {sessionId}");

                // Delete the session itself
                var sessionDeleted = await _storageService.DeleteSessionAsync(tenantId, sessionId);

                if (sessionDeleted)
                {
                    // Log audit entry with actual user identifier
                    await _storageService.LogAuditEntryAsync(
                        tenantId,
                        "DELETE",
                        "Session",
                        sessionId,
                        userIdentifier,
                        new Dictionary<string, string>
                        {
                            { "EventsDeleted", eventsDeleted.ToString() },
                            { "RuleResultsDeleted", ruleResultsDeleted.ToString() },
                            { "AppInstallSummariesDeleted", appSummariesDeleted.ToString() }
                        }
                    );

                    _logger.LogInformation($"Successfully deleted session {sessionId}");
                    var response = req.CreateResponse(HttpStatusCode.OK);
                    await response.WriteAsJsonAsync(new
                    {
                        success = true,
                        message = $"Session {sessionId} deleted successfully",
                        eventsDeleted,
                        ruleResultsDeleted,
                        appInstallSummariesDeleted = appSummariesDeleted
                    });
                    return response;
                }
                else
                {
                    _logger.LogWarning($"Session {sessionId} not found");
                    var response = req.CreateResponse(HttpStatusCode.NotFound);
                    await response.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = $"Session {sessionId} not found"
                    });
                    return response;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error deleting session {sessionId}");

                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "Internal server error"
                });

                return errorResponse;
            }
        }
    }
}
