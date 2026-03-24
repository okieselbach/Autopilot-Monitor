using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Sessions;

public class SearchSessionsFunction
{
    private readonly ILogger<SearchSessionsFunction> _logger;
    private readonly TableStorageService _storageService;

    public SearchSessionsFunction(ILogger<SearchSessionsFunction> logger, TableStorageService storageService)
    {
        _logger = logger;
        _storageService = storageService;
    }

    [Function("SearchSessions")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sessions/search")] HttpRequestData req)
        => await HandleAsync(req, isTenantScoped: true);

    [Function("SearchSessionsGlobal")]
    public async Task<HttpResponseData> RunGlobal(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/sessions/search")] HttpRequestData req)
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
                TpmSpecVersion = query["tpmSpecVersion"],
                AutopilotMode = query["autopilotMode"],
                DomainJoinMethod = query["domainJoinMethod"],
                ConnectionType = query["connectionType"],
                AgentVersion = query["agentVersion"],
                ImeAgentVersion = query["imeAgentVersion"],
                Limit = int.TryParse(query["limit"], out var lim) ? Math.Min(lim, 100) : 50,
            };

            if (bool.TryParse(query["isPreProvisioned"], out var ipp)) filter.IsPreProvisioned = ipp;
            if (bool.TryParse(query["isHybridJoin"], out var ihj)) filter.IsHybridJoin = ihj;
            if (bool.TryParse(query["tpmActivated"], out var ta)) filter.TpmActivated = ta;
            if (bool.TryParse(query["secureBootEnabled"], out var sbe)) filter.SecureBootEnabled = sbe;
            if (bool.TryParse(query["bitlockerEnabled"], out var ble)) filter.BitlockerEnabled = ble;
            if (bool.TryParse(query["hasSSD"], out var hs)) filter.HasSSD = hs;
            if (double.TryParse(query["minRamGB"], out var ram)) filter.MinRamGB = ram;
            if (DateTime.TryParse(query["startedAfter"], out var sa)) filter.StartedAfter = sa;
            if (DateTime.TryParse(query["startedBefore"], out var sb)) filter.StartedBefore = sb;

            var sessions = await _storageService.SearchSessionsAsync(tenantId, filter);

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
            _logger.LogError(ex, "Error searching sessions");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
            return err;
        }
    }
}
