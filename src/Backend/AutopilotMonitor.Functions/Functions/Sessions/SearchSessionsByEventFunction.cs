using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Sessions;

public class SearchSessionsByEventFunction
{
    private readonly ILogger<SearchSessionsByEventFunction> _logger;
    private readonly ISessionRepository _sessionRepo;

    public SearchSessionsByEventFunction(ILogger<SearchSessionsByEventFunction> logger, ISessionRepository sessionRepo)
    {
        _logger = logger;
        _sessionRepo = sessionRepo;
    }

    [Function("SearchSessionsByEvent")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "search/sessions-by-event")] HttpRequestData req)
        => await HandleAsync(req, isTenantScoped: true);

    [Function("SearchSessionsByEventGlobal")]
    public async Task<HttpResponseData> RunGlobal(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/search/sessions-by-event")] HttpRequestData req)
        => await HandleAsync(req, isTenantScoped: false);

    private async Task<HttpResponseData> HandleAsync(HttpRequestData req, bool isTenantScoped)
    {
        try
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);

            string? tenantId;
            if (isTenantScoped)
            {
                tenantId = TenantHelper.GetTenantId(req);
            }
            else
            {
                tenantId = query["tenantId"];
            }

            var eventType = query["eventType"];
            if (string.IsNullOrEmpty(eventType))
            {
                var badReq = req.CreateResponse(HttpStatusCode.BadRequest);
                await badReq.WriteAsJsonAsync(new { success = false, message = "eventType is required" });
                return badReq;
            }

            var limit = int.TryParse(query["limit"], out var lim) ? Math.Min(lim, 100) : 50;

            var sessions = await _sessionRepo.SearchSessionsByEventAsync(tenantId, eventType, null, null, null, limit);

            return await req.OkAsync(new
            {
                success = true,
                count = sessions.Count,
                sessions
            });
        }
        catch (Exception ex)
        {
            return await req.InternalServerErrorAsync(_logger, ex, "Search sessions by event");
        }
    }
}
