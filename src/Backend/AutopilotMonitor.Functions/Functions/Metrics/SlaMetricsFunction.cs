using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Metrics
{
    /// <summary>
    /// GET /api/metrics/sla?tenantId={tenantId}&amp;months=3
    /// Returns SLA compliance metrics for a tenant.
    /// </summary>
    public class SlaMetricsFunction
    {
        private readonly ILogger<SlaMetricsFunction> _logger;
        private readonly SlaMetricsService _slaMetricsService;

        public SlaMetricsFunction(
            ILogger<SlaMetricsFunction> logger,
            SlaMetricsService slaMetricsService)
        {
            _logger = logger;
            _slaMetricsService = slaMetricsService;
        }

        [Function("GetSlaMetrics")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "metrics/sla")]
            HttpRequestData req)
        {
            _logger.LogInformation("SLA metrics requested");

            try
            {
                string tenantId = TenantHelper.GetTenantId(req);

                // Parse optional months query parameter (default 3, max 6)
                int months = 3;
                var monthsParam = req.Url.Query?.Contains("months=") == true
                    ? System.Web.HttpUtility.ParseQueryString(req.Url.Query).Get("months")
                    : null;
                if (int.TryParse(monthsParam, out var parsedMonths))
                    months = parsedMonths;

                var metrics = await _slaMetricsService.ComputeSlaMetricsAsync(tenantId, months);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(metrics);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error computing SLA metrics");

                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "Failed to compute SLA metrics"
                });
                return errorResponse;
            }
        }
    }
}
