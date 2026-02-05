using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Extensions.SignalRService;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Functions
{
    public class SignalRAddToGroupFunction
    {
        private readonly ILogger<SignalRAddToGroupFunction> _logger;

        public SignalRAddToGroupFunction(ILogger<SignalRAddToGroupFunction> logger)
        {
            _logger = logger;
        }

        [Function("AddToGroup")]
        public async Task<AddToGroupOutput> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "realtime/groups/join")] HttpRequestData req)
        {
            try
            {
                // Parse request
                var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                var request = JsonConvert.DeserializeObject<AddToGroupRequest>(requestBody);

                if (string.IsNullOrEmpty(request?.ConnectionId) || string.IsNullOrEmpty(request?.GroupName))
                {
                    var errorResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                    await errorResponse.WriteAsJsonAsync(new { success = false, message = "ConnectionId and GroupName are required" });
                    return new AddToGroupOutput { HttpResponse = errorResponse };
                }

                // Extract session ID from group name if it's a session-specific group
                var logPrefix = ExtractLogPrefix(request.GroupName);
                _logger.LogInformation($"{logPrefix} AddToGroup: {request.GroupName}");

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    message = $"Added to group {request.GroupName}"
                });

                return new AddToGroupOutput
                {
                    HttpResponse = response,
                    SignalRGroupAction = new SignalRGroupAction(SignalRGroupActionType.Add)
                    {
                        GroupName = request.GroupName,
                        ConnectionId = request.ConnectionId
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding to group");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return new AddToGroupOutput { HttpResponse = errorResponse };
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

    public class AddToGroupRequest
    {
        public string? ConnectionId { get; set; }
        public string? GroupName { get; set; }
    }

    public class AddToGroupOutput
    {
        [HttpResult]
        public HttpResponseData? HttpResponse { get; set; }

        [SignalROutput(HubName = "autopilotmonitor")]
        public SignalRGroupAction? SignalRGroupAction { get; set; }
    }
}
