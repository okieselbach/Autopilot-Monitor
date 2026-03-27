using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Metrics;

public class MetricsSummaryFunction
{
    private readonly ILogger<MetricsSummaryFunction> _logger;
    private readonly IMetricsRepository _metricsRepo;

    public MetricsSummaryFunction(ILogger<MetricsSummaryFunction> logger, IMetricsRepository metricsRepo)
    {
        _logger = logger;
        _metricsRepo = metricsRepo;
    }

    [Function("MetricsSummary")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "metrics/summary")] HttpRequestData req)
    {
        try
        {
            var tenantId = TenantHelper.GetTenantId(req);
            var summary = await _metricsRepo.GetMetricsSummaryAsync(tenantId);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { success = true, summary });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting metrics summary");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
            return err;
        }
    }

    [Function("MetricsSummaryGlobal")]
    public async Task<HttpResponseData> RunGlobal(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/metrics/summary")] HttpRequestData req)
    {
        try
        {
            var summary = await _metricsRepo.GetMetricsSummaryAsync(null);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { success = true, summary });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting global metrics summary");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
            return err;
        }
    }
}
