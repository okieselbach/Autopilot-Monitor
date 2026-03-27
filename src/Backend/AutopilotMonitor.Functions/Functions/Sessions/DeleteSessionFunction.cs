using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Sessions
{
    public class DeleteSessionFunction
    {
        private readonly ILogger<DeleteSessionFunction> _logger;
        private readonly ISessionRepository _sessionRepo;
        private readonly IMaintenanceRepository _maintenanceRepo;

        public DeleteSessionFunction(
            ILogger<DeleteSessionFunction> logger,
            ISessionRepository sessionRepo,
            IMaintenanceRepository maintenanceRepo)
        {
            _logger = logger;
            _sessionRepo = sessionRepo;
            _maintenanceRepo = maintenanceRepo;
        }

        [Function("DeleteSession")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "sessions/{sessionId}")] HttpRequestData req,
            string sessionId)
        {
            _logger.LogInformation($"DeleteSession function processing request for session {sessionId}");

            try
            {
                // Authentication + TenantAdminOrGA authorization enforced by PolicyEnforcementMiddleware
                var tenantId = TenantHelper.GetTenantId(req);
                var userIdentifier = TenantHelper.GetUserIdentifier(req);

                _logger.LogInformation($"Deleting session {sessionId} for tenant {tenantId} by user {userIdentifier}");

                // Delete all related data for this session
                var eventsDeleted = await _maintenanceRepo.DeleteSessionEventsAsync(tenantId, sessionId);
                _logger.LogInformation($"Deleted {eventsDeleted} events for session {sessionId}");

                var ruleResultsDeleted = await _maintenanceRepo.DeleteSessionRuleResultsAsync(tenantId, sessionId);
                _logger.LogInformation($"Deleted {ruleResultsDeleted} rule results for session {sessionId}");

                var appSummariesDeleted = await _maintenanceRepo.DeleteSessionAppInstallSummariesAsync(tenantId, sessionId);
                _logger.LogInformation($"Deleted {appSummariesDeleted} app install summaries for session {sessionId}");

                // Delete the session itself
                var sessionDeleted = await _sessionRepo.DeleteSessionAsync(tenantId, sessionId);

                if (sessionDeleted)
                {
                    // Log audit entry with actual user identifier
                    await _maintenanceRepo.LogAuditEntryAsync(
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
