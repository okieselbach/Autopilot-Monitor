using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Admin
{
    /// <summary>
    /// Global Admin endpoint: returns operational events from the OpsEvents table.
    /// Supports optional category filter via query parameter.
    /// Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware.
    /// </summary>
    public class GetOpsEventsFunction
    {
        private readonly ILogger<GetOpsEventsFunction> _logger;
        private readonly IOpsEventRepository _repository;

        public GetOpsEventsFunction(
            ILogger<GetOpsEventsFunction> logger,
            IOpsEventRepository repository)
        {
            _logger = logger;
            _repository = repository;
        }

        [Function("GetOpsEvents")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/ops-events")] HttpRequestData req)
        {
            try
            {
                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                var category = query["category"];
                var maxResultsStr = query["maxResults"];
                var maxResults = 200;
                if (int.TryParse(maxResultsStr, out var parsed) && parsed > 0 && parsed <= 1000)
                    maxResults = parsed;

                List<OpsEventEntry> events;
                if (!string.IsNullOrWhiteSpace(category))
                {
                    events = await _repository.GetOpsEventsByCategoryAsync(category, maxResults);
                }
                else
                {
                    events = await _repository.GetOpsEventsAsync(maxResults);
                }

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    count = events.Count,
                    events
                });
                return response;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "Get ops events");
            }
        }
    }
}
