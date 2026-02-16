using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Service for performing health checks
/// </summary>
public class HealthCheckService
{
    private readonly ILogger<HealthCheckService> _logger;
    private readonly TableStorageService _tableStorageService;

    public HealthCheckService(
        ILogger<HealthCheckService> logger,
        TableStorageService tableStorageService)
    {
        _logger = logger;
        _tableStorageService = tableStorageService;
    }

    /// <summary>
    /// Performs all health checks and returns the results
    /// </summary>
    public async Task<HealthCheckResult> PerformAllChecksAsync()
    {
        var result = new HealthCheckResult
        {
            Timestamp = DateTime.UtcNow,
            Checks = new List<HealthCheck>()
        };

        var checks = await Task.WhenAll(
            CheckTableStorageAsync(),
            CheckFunctionsHostAsync()
        );

        result.Checks.AddRange(checks);
        result.OverallStatus = result.Checks.All(c => c.Status == "healthy") ? "healthy" : "unhealthy";

        return result;
    }

    /// <summary>
    /// Checks Azure Table Storage connectivity by performing a lightweight query
    /// </summary>
    private async Task<HealthCheck> CheckTableStorageAsync()
    {
        var check = new HealthCheck
        {
            Name = "Table Storage",
            Description = "Azure Table Storage connectivity"
        };

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await _tableStorageService.GetSessionsAsync("health-check", maxResults: 1);
            sw.Stop();

            check.Status = "healthy";
            check.Message = $"Table Storage reachable ({sw.ElapsedMilliseconds}ms)";
        }
        catch (Exception ex)
        {
            check.Status = "unhealthy";
            check.Message = $"Table Storage unreachable: {ex.Message}";
            _logger.LogError(ex, "Table Storage health check failed");
        }

        return check;
    }

    /// <summary>
    /// Checks that the Azure Functions host is running
    /// </summary>
    private Task<HealthCheck> CheckFunctionsHostAsync()
    {
        var uptime = DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();

        return Task.FromResult(new HealthCheck
        {
            Name = "Functions Host",
            Description = "Azure Functions host process",
            Status = "healthy",
            Message = $"Functions host running (uptime: {(int)uptime.TotalMinutes}m {uptime.Seconds}s)"
        });
    }
}

/// <summary>
/// Result of a health check operation
/// </summary>
public class HealthCheckResult
{
    public DateTime Timestamp { get; set; }
    public string OverallStatus { get; set; } = "unknown";
    public List<HealthCheck> Checks { get; set; } = new();
}

/// <summary>
/// Individual health check result
/// </summary>
public class HealthCheck
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Status { get; set; } = "unknown";
    public string Message { get; set; } = string.Empty;
    public Dictionary<string, object>? Details { get; set; }
}
