using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Sessions;

public class SearchSessionsByCveFunction
{
    private readonly ILogger<SearchSessionsByCveFunction> _logger;
    private readonly ISessionRepository _sessionRepo;

    public SearchSessionsByCveFunction(ILogger<SearchSessionsByCveFunction> logger, ISessionRepository sessionRepo)
    {
        _logger = logger;
        _sessionRepo = sessionRepo;
    }

    [Function("SearchSessionsByCve")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sessions/search-by-cve")] HttpRequestData req)
        => await HandleAsync(req, isTenantScoped: true);

    [Function("SearchSessionsByCveGlobal")]
    public async Task<HttpResponseData> RunGlobal(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/sessions/search-by-cve")] HttpRequestData req)
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

            var cveId = query["cveId"];
            if (string.IsNullOrEmpty(cveId))
            {
                var badReq = req.CreateResponse(HttpStatusCode.BadRequest);
                await badReq.WriteAsJsonAsync(new { success = false, message = "cveId is required" });
                return badReq;
            }

            double? minCvssScore = double.TryParse(query["minCvssScore"], out var mcs) ? mcs : null;
            var overallRisk = query["overallRisk"];
            var limit = int.TryParse(query["limit"], out var lim) ? Math.Min(lim, 100) : 50;

            var sessions = await _storageService.SearchSessionsByCveAsync(tenantId, cveId, minCvssScore, overallRisk, limit);

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
            _logger.LogError(ex, "Error searching sessions by CVE");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
            return err;
        }
    }
}
