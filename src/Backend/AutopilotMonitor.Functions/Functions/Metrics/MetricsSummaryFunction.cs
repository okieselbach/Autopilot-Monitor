using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Metrics;

public class MetricsSummaryFunction
{
    private readonly ILogger<MetricsSummaryFunction> _logger;
    private readonly TableStorageService _storageService;

    public MetricsSummaryFunction(ILogger<MetricsSummaryFunction> logger, TableStorageService storageService)
    {
        _logger = logger;
        _storageService = storageService;
    }

    [Function("MetricsSummary")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "metrics/summary")] HttpRequestData req)
    {
        try
        {
            var tenantId = TenantHelper.GetTenantId(req);
            var summary = await _storageService.GetMetricsSummaryAsync(tenantId);

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
            var summary = await _storageService.GetMetricsSummaryAsync(null);

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
