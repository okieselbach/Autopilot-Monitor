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
        public HttpResponseData Negotiate(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "realtime/negotiate")] HttpRequestData req,
            [SignalRConnectionInfoInput(HubName = "autopilotmonitor")] SignalRConnectionInfo connectionInfo)
        {
            _logger.LogInformation("SignalR negotiate request");

            var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
            response.WriteAsJsonAsync(connectionInfo);

            return response;
        }
    }
}
