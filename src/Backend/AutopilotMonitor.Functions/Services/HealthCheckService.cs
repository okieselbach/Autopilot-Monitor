using AutopilotMonitor.Shared;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Service for performing health checks
/// </summary>
public class HealthCheckService
{
    private readonly ILogger<HealthCheckService> _logger;
    private readonly AdminConfigurationService _adminConfigService;
    private readonly IHttpClientFactory _httpClientFactory;

    public HealthCheckService(
        ILogger<HealthCheckService> logger,
        AdminConfigurationService adminConfigService,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _adminConfigService = adminConfigService;
        _httpClientFactory = httpClientFactory;
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
            CheckStorageBackendAsync(),
            CheckProcessingBackendAsync(),
            CheckAgentBinariesAsync()
        );

        result.Checks.AddRange(checks);
        result.OverallStatus = result.Checks.All(c => c.Status == "healthy") ? "healthy" : "unhealthy";

        return result;
    }

    /// <summary>
    /// Checks Azure Table Storage connectivity by querying the admin configuration table
    /// </summary>
    private async Task<HealthCheck> CheckStorageBackendAsync()
    {
        var check = new HealthCheck
        {
            Name = "Storage Backend",
            Description = "Data storage connectivity"
        };

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await _adminConfigService.GetConfigurationAsync();
            sw.Stop();

            check.Status = "healthy";
            check.Message = $"Storage reachable ({sw.ElapsedMilliseconds}ms)";
        }
        catch (Exception ex)
        {
            check.Status = "unhealthy";
            check.Message = $"Storage unreachable: {ex.Message}";
            _logger.LogError(ex, "Storage backend health check failed");
        }

        return check;
    }

    /// <summary>
    /// Checks that the Azure Functions host is running
    /// </summary>
    private static Task<HealthCheck> CheckProcessingBackendAsync()
    {
        var uptime = DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();

        return Task.FromResult(new HealthCheck
        {
            Name = "Processing Backend",
            Description = "Application host process",
            Status = "healthy",
            Message = $"Host running (uptime: {(int)uptime.TotalMinutes}m {uptime.Seconds}s)"
        });
    }

    /// <summary>
    /// Checks that the agent binaries (ZIP) and bootstrap script (PS1) are reachable in blob storage
    /// </summary>
    private async Task<HealthCheck> CheckAgentBinariesAsync()
    {
        var check = new HealthCheck
        {
            Name = "Agent Binaries",
            Description = "Agent download package availability"
        };

        var zipUrl = $"{Constants.AgentBlobBaseUrl}/{Constants.AgentZipFileName}";
        var ps1Url = $"{Constants.AgentBlobBaseUrl}/Install-AutopilotMonitor.ps1";

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            var sw = System.Diagnostics.Stopwatch.StartNew();

            // HEAD requests to check availability without downloading
            var zipRequest = new HttpRequestMessage(HttpMethod.Head, zipUrl);
            var ps1Request = new HttpRequestMessage(HttpMethod.Head, ps1Url);

            var results = await Task.WhenAll(
                client.SendAsync(zipRequest),
                client.SendAsync(ps1Request)
            );

            sw.Stop();

            var zipResponse = results[0];
            var ps1Response = results[1];

            var zipOk = zipResponse.IsSuccessStatusCode;
            var ps1Ok = ps1Response.IsSuccessStatusCode;

            if (zipOk && ps1Ok)
            {
                check.Status = "healthy";
                check.Message = $"Agent package and bootstrap script available ({sw.ElapsedMilliseconds}ms)";
            }
            else
            {
                check.Status = "unhealthy";
                var issues = new List<string>();
                if (!zipOk) issues.Add($"Agent ZIP: {(int)zipResponse.StatusCode}");
                if (!ps1Ok) issues.Add($"Bootstrap script: {(int)ps1Response.StatusCode}");
                check.Message = $"Missing: {string.Join(", ", issues)}";
            }
        }
        catch (Exception ex)
        {
            check.Status = "unhealthy";
            check.Message = $"Blob storage unreachable: {ex.Message}";
            _logger.LogError(ex, "Agent binaries health check failed");
        }

        return check;
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
