using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Functions.Security
{
    /// <summary>
    /// Shared service for acquiring Microsoft Graph access tokens via client credentials flow.
    /// Used by AutopilotDeviceValidator and CorporateIdentifierValidator.
    /// </summary>
    public class GraphTokenService
    {
        private static readonly TimeSpan ConsentStatusTtl = TimeSpan.FromMinutes(2);

        private readonly ILogger<GraphTokenService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IMemoryCache _cache;
        private readonly IConfiguration _configuration;

        public GraphTokenService(
            ILogger<GraphTokenService> logger,
            IHttpClientFactory httpClientFactory,
            IMemoryCache cache,
            IConfiguration configuration)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _cache = cache;
            _configuration = configuration;
        }

        public async Task<GraphConsentStatusResult> GetConsentStatusAsync(string tenantId)
        {
            var cacheKey = $"graph-consent-status:{tenantId}";
            if (_cache.TryGetValue(cacheKey, out GraphConsentStatusResult? cached) && cached != null)
            {
                return cached;
            }

            var accessToken = await GetAccessTokenAsync(tenantId);
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

        public async Task<string?> GetAccessTokenAsync(string tenantId)
        {
            var clientId = _configuration["EntraId:ClientId"];
            var clientSecret = _configuration["EntraId:ClientSecret"];

            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            {
                _logger.LogError(
                    "Device validation is enabled but Entra ID app credentials are not configured. Set EntraId:ClientId and EntraId:ClientSecret.");
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
    }

    public class GraphConsentStatusResult
    {
        public bool IsConsented { get; set; }
        public string? Message { get; set; }
    }
}
