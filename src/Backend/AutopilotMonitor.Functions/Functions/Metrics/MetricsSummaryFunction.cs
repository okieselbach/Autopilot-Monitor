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
            var days = ParseDays(req);
            var summary = await _metricsRepo.GetMetricsSummaryAsync(tenantId, days);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { success = true, summary, windowDays = days });
            return response;
        }
        catch (Exception ex)
        {
            return await req.InternalServerErrorAsync(_logger, ex, "Get metrics summary");
        }
    }

    [Function("MetricsSummaryGlobal")]
    public async Task<HttpResponseData> RunGlobal(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/metrics/summary")] HttpRequestData req)
    {
        try
        {
            var days = ParseDays(req);
            var summary = await _metricsRepo.GetMetricsSummaryAsync(null, days);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { success = true, summary, windowDays = days });
            return response;
        }
        catch (Exception ex)
        {
            return await req.InternalServerErrorAsync(_logger, ex, "Get global metrics summary");
        }
    }

    private static int ParseDays(HttpRequestData req)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var raw = query["days"];
        var days = 30;
        if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out var parsed) && parsed > 0)
            days = parsed;
        if (days < 1) days = 1;
        if (days > 365) days = 365;
        return days;
    }
}
