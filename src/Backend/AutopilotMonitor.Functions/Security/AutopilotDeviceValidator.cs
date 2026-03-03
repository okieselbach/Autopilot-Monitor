using System;
using System.Linq;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Functions.Security
{
    /// <summary>
    /// Validates devices against Intune Autopilot device registration via Microsoft Graph.
    /// Caches positive/negative lookups to reduce Graph traffic.
    /// </summary>
    public class AutopilotDeviceValidator
    {
        private static readonly TimeSpan PositiveCacheTtl = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromMinutes(5);

        private readonly ILogger<AutopilotDeviceValidator> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IMemoryCache _cache;
        private readonly GraphTokenService _graphTokenService;

        public AutopilotDeviceValidator(
            ILogger<AutopilotDeviceValidator> logger,
            IHttpClientFactory httpClientFactory,
            IMemoryCache cache,
            GraphTokenService graphTokenService)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _cache = cache;
            _graphTokenService = graphTokenService;
        }

        public async Task<AutopilotDeviceValidationResult> ValidateAutopilotDeviceAsync(
            string tenantId,
            string? serialNumber,
            string? sessionId = null)
        {
            if (string.IsNullOrWhiteSpace(serialNumber))
            {
                return new AutopilotDeviceValidationResult
                {
                    IsValid = false,
                    ErrorMessage = "Serial number header not provided"
                };
            }

            var normalizedSerial = serialNumber.Trim();
            var cacheKey = BuildCacheKey(tenantId, normalizedSerial, sessionId);
            if (_cache.TryGetValue(cacheKey, out AutopilotDeviceValidationResult? cached) && cached != null)
            {
                return cached;
            }

            try
            {
                var accessToken = await _graphTokenService.GetAccessTokenAsync(tenantId);
                if (string.IsNullOrEmpty(accessToken))
                {
                    // Do not cache token-acquisition failures — this is a backend/propagation issue,
                    // not a device issue. The next enrollment attempt should retry immediately.
                    return new AutopilotDeviceValidationResult
                    {
                        IsValid = false,
                        SerialNumber = normalizedSerial,
                        ErrorMessage = "Graph access token could not be acquired"
                    };
                }

                var graphClient = _httpClientFactory.CreateClient();
                graphClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                // For windowsAutopilotDeviceIdentities, eq on serialNumber is unreliable and often returns 400.
                // Use contains for server-side narrowing, then perform exact match client-side.
                var escapedSerial = normalizedSerial.Replace("'", "''");
                var filter = Uri.EscapeDataString($"contains(serialNumber,'{escapedSerial}')");
                var graphUrl = $"https://graph.microsoft.com/v1.0/deviceManagement/windowsAutopilotDeviceIdentities?$top=100&$filter={filter}";

                var response = await graphClient.GetAsync(graphUrl);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Autopilot device validation Graph query failed for tenant {TenantId}. Status: {StatusCode}. Body: {ResponseBody}",
                        tenantId,
                        (int)response.StatusCode,
                        responseBody);

                    return CacheAndReturn(cacheKey, new AutopilotDeviceValidationResult
                    {
                        IsValid = false,
                        SerialNumber = normalizedSerial,
                        ErrorMessage = $"Graph query failed with status {(int)response.StatusCode}"
                    }, isPositive: false);
                }

                var data = JsonConvert.DeserializeObject<JObject>(responseBody);
                var devices = data?["value"] as JArray;
                if (devices == null || devices.Count == 0)
                {
                    return CacheAndReturn(cacheKey, new AutopilotDeviceValidationResult
                    {
                        IsValid = false,
                        SerialNumber = normalizedSerial,
                        ErrorMessage = $"Device with serial '{normalizedSerial}' is not registered in Autopilot"
                    }, isPositive: false);
                }

                // Exact-match guard to avoid false positives from contains(...)
                var exactDevice = devices
                    .FirstOrDefault(d => string.Equals(
                        d?["serialNumber"]?.ToString()?.Trim(),
                        normalizedSerial,
                        StringComparison.OrdinalIgnoreCase));

                if (exactDevice == null)
                {
                    return CacheAndReturn(cacheKey, new AutopilotDeviceValidationResult
                    {
                        IsValid = false,
                        SerialNumber = normalizedSerial,
                        ErrorMessage = $"Device with serial '{normalizedSerial}' is not registered in Autopilot"
                    }, isPositive: false);
                }

                var result = new AutopilotDeviceValidationResult
                {
                    IsValid = true,
                    SerialNumber = normalizedSerial,
                    AutopilotDeviceId = exactDevice["id"]?.ToString()
                };

                _logger.LogInformation(
                    "Autopilot device validation succeeded for tenant {TenantId}, session {SessionId}, serial {SerialNumber}, autopilotId {AutopilotDeviceId}",
                    tenantId,
                    sessionId ?? "<none>",
                    normalizedSerial,
                    result.AutopilotDeviceId ?? "<none>");

                return CacheAndReturn(cacheKey, result, isPositive: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error during Autopilot device validation for tenant {TenantId}, session {SessionId}, serial {SerialNumber}",
                    tenantId,
                    sessionId ?? "<none>",
                    normalizedSerial);

                return CacheAndReturn(cacheKey, new AutopilotDeviceValidationResult
                {
                    IsValid = false,
                    SerialNumber = normalizedSerial,
                    ErrorMessage = $"Error during Autopilot device validation: {ex.Message}"
                }, isPositive: false);
            }
        }

        private static string BuildCacheKey(string tenantId, string serialNumber, string? sessionId)
        {
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                return $"autopilot-device-validation:{tenantId}:{sessionId}:{serialNumber}";
            }

            return $"autopilot-device-validation:{tenantId}:{serialNumber}";
        }

        private AutopilotDeviceValidationResult CacheAndReturn(
            string cacheKey,
            AutopilotDeviceValidationResult result,
            bool isPositive)
        {
            var ttl = isPositive ? PositiveCacheTtl : NegativeCacheTtl;
            _cache.Set(cacheKey, result, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ttl
            });

            return result;
        }
    }

    public class AutopilotDeviceValidationResult
    {
        public bool IsValid { get; set; }
        public string? SerialNumber { get; set; }
        public string? AutopilotDeviceId { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
