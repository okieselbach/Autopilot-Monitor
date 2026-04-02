using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Sessions;

public class SearchSessionsFunction
{
    private readonly ILogger<SearchSessionsFunction> _logger;
    private readonly ISessionRepository _sessionRepo;

    public SearchSessionsFunction(ILogger<SearchSessionsFunction> logger, ISessionRepository sessionRepo)
    {
        _logger = logger;
        _sessionRepo = sessionRepo;
    }

    [Function("SearchSessions")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "search/sessions")] HttpRequestData req)
        => await HandleAsync(req, isTenantScoped: true);

    [Function("SearchSessionsGlobal")]
    public async Task<HttpResponseData> RunGlobal(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/search/sessions")] HttpRequestData req)
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
                // Global: tenantId is optional filter
                tenantId = query["tenantId"];
            }

            var filter = new SessionSearchFilter
            {
                Status = query["status"],
                SerialNumber = query["serialNumber"],
                DeviceName = query["deviceName"],
                Manufacturer = query["manufacturer"],
                Model = query["model"],
                OsBuild = query["osBuild"],
                EnrollmentType = query["enrollmentType"],
                GeoCountry = query["geoCountry"],
                AgentVersion = query["agentVersion"],
                ImeAgentVersion = query["imeAgentVersion"],
                Limit = int.TryParse(query["limit"], out var lim) ? Math.Min(lim, 100) : 50,
            };

            if (bool.TryParse(query["isPreProvisioned"], out var ipp)) filter.IsPreProvisioned = ipp;
            if (bool.TryParse(query["isHybridJoin"], out var ihj)) filter.IsHybridJoin = ihj;
            if (DateTime.TryParse(query["startedAfter"], out var sa)) filter.StartedAfter = sa;
            if (DateTime.TryParse(query["startedBefore"], out var sb)) filter.StartedBefore = sb;

            // Dynamic device property filters: any query param starting with "prop."
            // e.g. ?prop.tpm_status.specVersion=2.0&prop.hardware_spec.ramTotalGB=>=8
            var deviceProperties = new Dictionary<string, string>();
            foreach (string? key in query.AllKeys)
            {
                if (key != null && key.StartsWith("prop.", StringComparison.OrdinalIgnoreCase))
                {
                    var propName = key.Substring(5);
                    var value = query[key];
                    if (!string.IsNullOrEmpty(value))
                        deviceProperties[propName] = value;
                }
            }
            if (deviceProperties.Count > 0)
                filter.DeviceProperties = deviceProperties;

            var sessions = await _sessionRepo.SearchSessionsAsync(tenantId, filter);

            return await req.OkAsync(new
            {
                success = true,
                count = sessions.Count,
                sessions
            });
        }
        catch (Exception ex)
        {
            return await req.InternalServerErrorAsync(_logger, ex, "Search sessions");
        }
    }
}
