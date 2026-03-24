using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Sessions;

public class SearchSessionsByEventFunction
{
    private readonly ILogger<SearchSessionsByEventFunction> _logger;
    private readonly TableStorageService _storageService;

    public SearchSessionsByEventFunction(ILogger<SearchSessionsByEventFunction> logger, TableStorageService storageService)
    {
        _logger = logger;
        _storageService = storageService;
    }

    [Function("SearchSessionsByEvent")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sessions/search-by-event")] HttpRequestData req)
        => await HandleAsync(req, isTenantScoped: true);

    [Function("SearchSessionsByEventGlobal")]
    public async Task<HttpResponseData> RunGlobal(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/sessions/search-by-event")] HttpRequestData req)
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

            var sessions = await _storageService.SearchSessionsByEventAsync(tenantId, eventType, null, null, null, limit);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                count = sessions.Count,
                sessions
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching sessions by event");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
            return err;
        }
    }
}
