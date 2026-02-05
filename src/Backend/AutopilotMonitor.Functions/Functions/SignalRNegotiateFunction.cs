using System.Net;
using AutopilotMonitor.Functions.Helpers;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Extensions.SignalRService;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions
{
    public class SignalRNegotiateFunction
    {
        private readonly ILogger<SignalRNegotiateFunction> _logger;

        public SignalRNegotiateFunction(ILogger<SignalRNegotiateFunction> logger)
        {
            _logger = logger;
        }

        [Function("negotiate")]
        public async Task<HttpResponseData> Negotiate(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "realtime/negotiate")] HttpRequestData req,
            [SignalRConnectionInfoInput(HubName = "autopilotmonitor")] SignalRConnectionInfo connectionInfo)
        {
            _logger.LogInformation("SignalR negotiate request");

            // Validate authentication (middleware already validated JWT)
            // Azure Functions Isolated Worker: Check FunctionContext.Items first
            bool isAuthenticated = false;

            if (req.FunctionContext.Items.TryGetValue("ClaimsPrincipal", out var principalObj)
                && principalObj is System.Security.Claims.ClaimsPrincipal principal)
            {
                isAuthenticated = principal.Identity?.IsAuthenticated == true;
            }
            else
            {
                // Fallback to HTTP context
                var httpContext = req.FunctionContext.GetHttpContext();
                isAuthenticated = httpContext?.User?.Identity?.IsAuthenticated == true;
            }

            if (!isAuthenticated)
            {
                _logger.LogWarning("Unauthenticated SignalR negotiate attempt");
                var unauthorizedResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauthorizedResponse.WriteAsJsonAsync(new { success = false, message = "Authentication required" });
                return unauthorizedResponse;
            }

            var userEmail = TenantHelper.GetUserIdentifier(req);
            _logger.LogInformation($"SignalR connection negotiated for user: {userEmail}");

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(connectionInfo);

            return response;
        }
    }
}
