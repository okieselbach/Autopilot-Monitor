using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Metrics
{
    public class GetGalacticGeographicLocationSessionsFunction
    {
        private readonly ILogger<GetGalacticGeographicLocationSessionsFunction> _logger;
        private readonly TableStorageService _storageService;

        public GetGalacticGeographicLocationSessionsFunction(
            ILogger<GetGalacticGeographicLocationSessionsFunction> logger,
            TableStorageService storageService)
        {
            _logger = logger;
            _storageService = storageService;
        }

        [Function("GetGalacticGeographicLocationSessions")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "galactic/metrics/geographic/sessions")] HttpRequestData req)
        {
            try
            {
                // Authentication + GalacticAdminOnly authorization enforced by PolicyEnforcementMiddleware
                var userEmail = TenantHelper.GetUserIdentifier(req);

                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                var locationKey = query["locationKey"];
                if (string.IsNullOrEmpty(locationKey))
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { success = false, message = "locationKey parameter is required" });
                    return badRequest;
                }

                var daysParam = query["days"];
                int days = 30;
                if (!string.IsNullOrEmpty(daysParam) && int.TryParse(daysParam, out var parsedDays) && parsedDays > 0)
                    days = parsedDays;

                var groupBy = query["groupBy"] ?? "city";

                _logger.LogInformation("Fetching galactic sessions for location '{LocationKey}' ({Days}d, groupBy={GroupBy}) (User: {UserEmail})",
                    locationKey, days, groupBy, userEmail);

                var cutoff = DateTime.UtcNow.AddDays(-days);
                var sessions = await _storageService.GetSessionsByDateRangeAsync(cutoff, DateTime.UtcNow.AddDays(1));

                var filtered = GetGeographicLocationSessionsFunction.FilterSessionsByLocation(sessions, locationKey, groupBy);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new { success = true, sessions = filtered, totalCount = filtered.Count });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching galactic geographic location sessions");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return errorResponse;
            }
        }
    }
}
