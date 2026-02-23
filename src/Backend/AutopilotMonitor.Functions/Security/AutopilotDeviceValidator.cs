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
    /// Validates devices against Intune Autopilot device registration via Microsoft Graph.
    /// Caches positive/negative lookups to reduce Graph traffic.
    /// </summary>
    public class AutopilotDeviceValidator
    {
        private static readonly TimeSpan PositiveCacheTtl = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ConsentStatusTtl = TimeSpan.FromMinutes(2);

        private readonly ILogger<AutopilotDeviceValidator> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IMemoryCache _cache;
        private readonly IConfiguration _configuration;

        public AutopilotDeviceValidator(
            ILogger<AutopilotDeviceValidator> logger,
            IHttpClientFactory httpClientFactory,
            IMemoryCache cache,
            IConfiguration configuration)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _cache = cache;
            _configuration = configuration;
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
                var accessToken = await GetGraphAccessTokenAsync(tenantId);
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

        public async Task<GraphConsentStatusResult> GetConsentStatusAsync(string tenantId)
        {
            var cacheKey = $"autopilot-device-validation-consent:{tenantId}";
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
                    "Autopilot device validation is enabled but Entra ID app credentials are not configured. Set EntraId:ClientId and EntraId:ClientSecret.");
                return null;
            }

            var tokenUrl = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";

            // Retry with backoff to handle Azure AD consent propagation delays.
            // After admin consent is granted, the service principal may not be immediately
            // available in the tenant — Azure AD typically propagates within 30-90 seconds.
            var retryDelays = new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30) };
            string? responseBody = null;
            int attempt = 0;

            while (true)
            {
                var tokenClient = _httpClientFactory.CreateClient();
                var body = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["client_secret"] = clientSecret,
                    ["scope"] = "https://graph.microsoft.com/.default",
                    ["grant_type"] = "client_credentials"
                });

                var response = await tokenClient.PostAsync(tokenUrl, body);
                responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var tokenJson = JsonConvert.DeserializeObject<JObject>(responseBody);
                    return tokenJson?["access_token"]?.ToString();
                }

                // Only retry on transient consent-propagation errors (unauthorized_client, invalid_client).
                // Do not retry on permanent errors like invalid credentials or tenant not found.
                var isRetryable = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    || response.StatusCode == System.Net.HttpStatusCode.BadRequest
                    && (responseBody.Contains("unauthorized_client", StringComparison.OrdinalIgnoreCase)
                        || responseBody.Contains("AADSTS700016", StringComparison.OrdinalIgnoreCase)
                        || responseBody.Contains("AADSTS7000215", StringComparison.OrdinalIgnoreCase));

                if (!isRetryable || attempt >= retryDelays.Length)
                {
                    _logger.LogWarning(
                        "Failed to acquire Graph token for tenant {TenantId} after {Attempts} attempt(s). Status: {StatusCode}. Body: {ResponseBody}",
                        tenantId,
                        attempt + 1,
                        (int)response.StatusCode,
                        responseBody);
                    return null;
                }

                var delay = retryDelays[attempt];
                _logger.LogInformation(
                    "Graph token acquisition for tenant {TenantId} returned {StatusCode} (attempt {Attempt}/{MaxAttempts}). Retrying in {Delay}s — likely Azure AD consent propagation delay.",
                    tenantId,
                    (int)response.StatusCode,
                    attempt + 1,
                    retryDelays.Length + 1,
                    delay.TotalSeconds);

                await Task.Delay(delay);
                attempt++;
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

    public class GraphConsentStatusResult
    {
        public bool IsConsented { get; set; }
        public string? Message { get; set; }
    }
}
