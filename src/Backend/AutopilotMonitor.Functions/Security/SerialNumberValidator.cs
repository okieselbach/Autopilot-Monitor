using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Functions.Security
{
    /// <summary>
    /// Validates serial numbers against Intune Autopilot device registration via Microsoft Graph.
    /// Caches positive/negative lookups to reduce Graph traffic.
    /// </summary>
    public class SerialNumberValidator
    {
        private static readonly TimeSpan PositiveCacheTtl = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ConsentStatusTtl = TimeSpan.FromMinutes(2);

        private readonly ILogger<SerialNumberValidator> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IMemoryCache _cache;
        private readonly IConfiguration _configuration;

        public SerialNumberValidator(
            ILogger<SerialNumberValidator> logger,
            IHttpClientFactory httpClientFactory,
            IMemoryCache cache,
            IConfiguration configuration)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _cache = cache;
            _configuration = configuration;
        }

        public async Task<SerialNumberValidationResult> ValidateSerialNumberAsync(
            string tenantId,
            string? serialNumber,
            string? sessionId = null)
        {
            if (string.IsNullOrWhiteSpace(serialNumber))
            {
                return new SerialNumberValidationResult
                {
                    IsValid = false,
                    ErrorMessage = "Serial number header not provided"
                };
            }

            var normalizedSerial = serialNumber.Trim();
            var cacheKey = BuildCacheKey(tenantId, normalizedSerial, sessionId);
            if (_cache.TryGetValue(cacheKey, out SerialNumberValidationResult? cached) && cached != null)
            {
                return cached;
            }

            try
            {
                var accessToken = await GetGraphAccessTokenAsync(tenantId);
                if (string.IsNullOrEmpty(accessToken))
                {
                    return CacheAndReturn(cacheKey, new SerialNumberValidationResult
                    {
                        IsValid = false,
                        SerialNumber = normalizedSerial,
                        ErrorMessage = "Graph access token could not be acquired"
                    }, isPositive: false);
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
                        "Graph serial validation failed for tenant {TenantId}. Status: {StatusCode}. Body: {ResponseBody}",
                        tenantId,
                        (int)response.StatusCode,
                        responseBody);

                    return CacheAndReturn(cacheKey, new SerialNumberValidationResult
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
                    return CacheAndReturn(cacheKey, new SerialNumberValidationResult
                    {
                        IsValid = false,
                        SerialNumber = normalizedSerial,
                        ErrorMessage = $"Serial number '{normalizedSerial}' is not registered in Autopilot"
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
                    return CacheAndReturn(cacheKey, new SerialNumberValidationResult
                    {
                        IsValid = false,
                        SerialNumber = normalizedSerial,
                        ErrorMessage = $"Serial number '{normalizedSerial}' is not registered in Autopilot"
                    }, isPositive: false);
                }

                var result = new SerialNumberValidationResult
                {
                    IsValid = true,
                    SerialNumber = normalizedSerial,
                    AutopilotDeviceId = exactDevice["id"]?.ToString()
                };

                _logger.LogInformation(
                    "Serial number validation succeeded for tenant {TenantId}, session {SessionId}, serial {SerialNumber}, autopilotId {AutopilotDeviceId}",
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
                    "Error validating serial number for tenant {TenantId}, session {SessionId}, serial {SerialNumber}",
                    tenantId,
                    sessionId ?? "<none>",
                    normalizedSerial);

                return CacheAndReturn(cacheKey, new SerialNumberValidationResult
                {
                    IsValid = false,
                    SerialNumber = normalizedSerial,
                    ErrorMessage = $"Error validating serial number: {ex.Message}"
                }, isPositive: false);
            }
        }

        public async Task<GraphConsentStatusResult> GetConsentStatusAsync(string tenantId)
        {
            var cacheKey = $"serial-validation-consent:{tenantId}";
            if (_cache.TryGetValue(cacheKey, out GraphConsentStatusResult? cached) && cached != null)
            {
                return cached;
            }

            var accessToken = await GetGraphAccessTokenAsync(tenantId);
            var result = new GraphConsentStatusResult
            {
                IsConsented = !string.IsNullOrWhiteSpace(accessToken),
                Message = string.IsNullOrWhiteSpace(accessToken)
                    ? "Admin consent for Graph application permissions is missing or app credentials are invalid."
                    : "Admin consent is available for this tenant."
            };

            _cache.Set(cacheKey, result, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ConsentStatusTtl
            });

            return result;
        }

        private async Task<string?> GetGraphAccessTokenAsync(string tenantId)
        {
            var clientId = _configuration["EntraId:ClientId"];
            var clientSecret = _configuration["EntraId:ClientSecret"];

            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            {
                _logger.LogError(
                    "Serial validation is enabled but Entra ID app credentials are not configured. Set EntraId:ClientId and EntraId:ClientSecret.");
                return null;
            }

            var tokenClient = _httpClientFactory.CreateClient();
            var tokenUrl = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";
            var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["scope"] = "https://graph.microsoft.com/.default",
                ["grant_type"] = "client_credentials"
            });

            var response = await tokenClient.PostAsync(tokenUrl, body);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Failed to acquire Graph token for tenant {TenantId}. Status: {StatusCode}. Body: {ResponseBody}",
                    tenantId,
                    (int)response.StatusCode,
                    responseBody);
                return null;
            }

            var tokenJson = JsonConvert.DeserializeObject<JObject>(responseBody);
            return tokenJson?["access_token"]?.ToString();
        }

        private static string BuildCacheKey(string tenantId, string serialNumber, string? sessionId)
        {
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                return $"serial-validation:{tenantId}:{sessionId}:{serialNumber}";
            }

            return $"serial-validation:{tenantId}:{serialNumber}";
        }

        private SerialNumberValidationResult CacheAndReturn(
            string cacheKey,
            SerialNumberValidationResult result,
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

    public class SerialNumberValidationResult
    {
        public bool IsValid { get; set; }
        public string? SerialNumber { get; set; }
        public string? AutopilotDeviceId { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public class GraphConsentStatusResult
    {
        public bool IsConsented { get; set; }
        public string? Message { get; set; }
    }
}
