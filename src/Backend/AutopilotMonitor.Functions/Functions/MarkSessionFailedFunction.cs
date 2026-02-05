using System.Net;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Extensions.SignalRService;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions
{
    public class MarkSessionFailedFunction
    {
        private readonly ILogger<MarkSessionFailedFunction> _logger;
        private readonly TableStorageService _storageService;

        public MarkSessionFailedFunction(ILogger<MarkSessionFailedFunction> logger, TableStorageService storageService)
        {
            _logger = logger;
            _storageService = storageService;
        }

        [Function("MarkSessionFailed")]
        public async Task<MarkSessionFailedOutput> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sessions/{sessionId}/mark-failed")] HttpRequestData req,
            string sessionId)
        {
            _logger.LogInformation($"MarkSessionFailed function processing request for session {sessionId}");

            try
            {
                // Get tenant ID from query parameter (for now, default to demo GUID)
                var tenantId = req.Query["tenantId"] ?? "deadbeef-dead-beef-dead-beefdeadbeef";

                _logger.LogInformation($"Marking session {sessionId} as failed for tenant {tenantId}");

                // Update session status to Failed with manual failure reason
                var success = await _storageService.UpdateSessionStatusAsync(
                    tenantId,
                    sessionId,
                    SessionStatus.Failed,
                    null, // Keep current phase
                    "Manually marked as failed by administrator"
                );

                if (success)
                {
                    // Log audit entry
                    await _storageService.LogAuditEntryAsync(
                        tenantId,
                        "UPDATE",
                        "Session",
                        sessionId,
                        "Admin", // TODO: Get from authenticated user
                        new Dictionary<string, string>
                        {
                            { "Action", "MarkAsFailed" },
                            { "Reason", "Manually marked as failed" }
                        }
                    );

                    // Retrieve updated session data to include in SignalR message
                    var updatedSession = await _storageService.GetSessionAsync(tenantId, sessionId);

                    _logger.LogInformation($"Successfully marked session {sessionId} as failed");
                    var response = req.CreateResponse(HttpStatusCode.OK);
                    await response.WriteAsJsonAsync(new
                    {
                        success = true,
                        message = $"Session {sessionId} marked as failed"
                    });

                    // Send SignalR notification to update all clients
                    var signalRMessage = new SignalRMessageAction("newevents")
                    {
                        GroupName = $"tenant-{tenantId}",
                        Arguments = new[] { new {
                            sessionId = sessionId,
                            tenantId = tenantId,
                            eventCount = 0,
                            session = updatedSession
                        } }
                    };

                    return new MarkSessionFailedOutput
                    {
                        HttpResponse = response,
                        SignalRMessages = new[] { signalRMessage }
                    };
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
                    return new MarkSessionFailedOutput { HttpResponse = response };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error marking session {sessionId} as failed");

                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "Internal server error"
                });

                return new MarkSessionFailedOutput { HttpResponse = errorResponse };
            }
        }
    }

    public class MarkSessionFailedOutput
    {
        [HttpResult]
        public HttpResponseData? HttpResponse { get; set; }

        [SignalROutput(HubName = "autopilotmonitor")]
        public SignalRMessageAction[]? SignalRMessages { get; set; }
    }
}
