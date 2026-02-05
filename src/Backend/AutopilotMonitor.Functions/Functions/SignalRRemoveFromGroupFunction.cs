using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Extensions.SignalRService;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Functions
{
    public class SignalRRemoveFromGroupFunction
    {
        private readonly ILogger<SignalRRemoveFromGroupFunction> _logger;

        public SignalRRemoveFromGroupFunction(ILogger<SignalRRemoveFromGroupFunction> logger)
        {
            _logger = logger;
        }

        [Function("RemoveFromGroup")]
        public async Task<RemoveFromGroupOutput> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "realtime/groups/leave")] HttpRequestData req)
        {
            try
            {
                // Parse request
                var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                var request = JsonConvert.DeserializeObject<RemoveFromGroupRequest>(requestBody);

                if (string.IsNullOrEmpty(request?.ConnectionId) || string.IsNullOrEmpty(request?.GroupName))
                {
                    var errorResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                    await errorResponse.WriteAsJsonAsync(new { success = false, message = "ConnectionId and GroupName are required" });
                    return new RemoveFromGroupOutput { HttpResponse = errorResponse };
                }

                // Extract session ID from group name if it's a session-specific group
                var logPrefix = ExtractLogPrefix(request.GroupName);
                _logger.LogInformation($"{logPrefix} RemoveFromGroup: {request.GroupName}");

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    message = $"Removed from group {request.GroupName}"
                });

                return new RemoveFromGroupOutput
                {
                    HttpResponse = response,
                    SignalRGroupAction = new SignalRGroupAction(SignalRGroupActionType.Remove)
                    {
                        GroupName = request.GroupName,
                        ConnectionId = request.ConnectionId
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing from group");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return new RemoveFromGroupOutput { HttpResponse = errorResponse };
            }
        }

        private string ExtractLogPrefix(string groupName)
        {
            // Extract session ID from group name: "session-{tenantId}-{sessionId}"
            if (groupName.StartsWith("session-"))
            {
                var parts = groupName.Split('-');
                if (parts.Length > 2)
                {
                    var sessionId = string.Join("-", parts.Skip(parts.Length - 5).Take(5)); // Last 5 parts form the GUID
                    return $"[Session: {sessionId.Substring(0, Math.Min(8, sessionId.Length))}]";
                }
            }
            // For tenant groups: "tenant-{tenantId}"
            return $"[Group: {groupName.Substring(0, Math.Min(20, groupName.Length))}]";
        }
    }

    public class RemoveFromGroupRequest
    {
        public string? ConnectionId { get; set; }
        public string? GroupName { get; set; }
    }

    public class RemoveFromGroupOutput
    {
        [HttpResult]
        public HttpResponseData? HttpResponse { get; set; }

        [SignalROutput(HubName = "autopilotmonitor")]
        public SignalRGroupAction? SignalRGroupAction { get; set; }
    }
}
