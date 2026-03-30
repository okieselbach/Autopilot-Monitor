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

            var tokenResult = await GetAccessTokenAsync(tenantId);
            var result = new GraphConsentStatusResult
            {
                IsConsented = !string.IsNullOrWhiteSpace(tokenResult.AccessToken),
                IsTransient = tokenResult.IsTransient,
                Message = !string.IsNullOrWhiteSpace(tokenResult.AccessToken)
                    ? "Admin consent is available for this tenant."
                    : tokenResult.IsTransient
                        ? "Could not verify consent status due to a transient error. Will retry on next request."
                        : "Admin consent for Graph application permissions is missing or app credentials are invalid."
            };

            // Only cache definitive results — never cache transient failures
            if (!tokenResult.IsTransient)
            {
                _cache.Set(cacheKey, result, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = ConsentStatusTtl
                });
            }
            else
            {
                _logger.LogWarning(
                    "Consent status check for tenant {TenantId} returned transient error — result NOT cached",
                    tenantId);
            }

            return result;
        }

        public async Task<GraphTokenResult> GetAccessTokenAsync(string tenantId)
        {
            var clientId = _configuration["EntraId:ClientId"];
            var clientSecret = _configuration["EntraId:ClientSecret"];

            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            {
                _logger.LogError(
                    "Device validation is enabled but Entra ID app credentials are not configured. Set EntraId:ClientId and EntraId:ClientSecret.");
                return GraphTokenResult.PermanentFailure();
            }

            var tokenUrl = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";

            // Retry with backoff to handle Azure AD consent propagation delays.
            // After admin consent is granted, the service principal may not be immediately
            // available in the tenant — Azure AD typically propagates within 30-90 seconds.
            var retryDelays = new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30) };
            string? responseBody = null;
            int attempt = 0;
            bool lastAttemptWasTransient = false;

            while (true)
            {
                try
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
                        var accessToken = tokenJson?["access_token"]?.ToString();
                        return GraphTokenResult.Success(accessToken);
                    }

                    // Classify the error: consent-propagation errors are retryable but ultimately permanent (no consent).
                    // Server errors (500, 502, 503, 504) and timeouts (408) are truly transient.
                    var statusCode = (int)response.StatusCode;
                    var isConsentError = response.StatusCode == System.Net.HttpStatusCode.BadRequest
                        && (responseBody.Contains("unauthorized_client", StringComparison.OrdinalIgnoreCase)
                            || responseBody.Contains("AADSTS700016", StringComparison.OrdinalIgnoreCase)
                            || responseBody.Contains("AADSTS7000215", StringComparison.OrdinalIgnoreCase));
                    var isServerError = statusCode == 408 || statusCode == 429
                        || statusCode >= 500 && statusCode <= 599;
                    var isRetryable = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                        || isConsentError || isServerError;

                    lastAttemptWasTransient = isServerError;

                    if (!isRetryable || attempt >= retryDelays.Length)
                    {
                        _logger.LogWarning(
                            "Failed to acquire Graph token for tenant {TenantId} after {Attempts} attempt(s). Status: {StatusCode}. Body: {ResponseBody}",
                            tenantId,
                            attempt + 1,
                            statusCode,
                            responseBody);

                        // Server errors are transient (infrastructure issue), consent errors are permanent (no consent granted)
                        return lastAttemptWasTransient
                            ? GraphTokenResult.TransientFailure()
                            : GraphTokenResult.PermanentFailure();
                    }

                    var delay = retryDelays[attempt];

                    // Respect Retry-After header from Azure AD / Graph throttling, but cap
                    // at 120s to prevent a misbehaving server from blocking indefinitely
                    if (response.Headers.RetryAfter?.Delta is TimeSpan retryAfterDelta && retryAfterDelta > delay)
                    {
                        delay = retryAfterDelta > TimeSpan.FromSeconds(120)
                            ? TimeSpan.FromSeconds(120)
                            : retryAfterDelta;
                    }

                    _logger.LogInformation(
                        "Graph token acquisition for tenant {TenantId} returned {StatusCode} (attempt {Attempt}/{MaxAttempts}). Retrying in {Delay}s — likely Azure AD consent propagation delay.",
                        tenantId,
                        statusCode,
                        attempt + 1,
                        retryDelays.Length + 1,
                        delay.TotalSeconds);

                    await Task.Delay(delay);
                    attempt++;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    // Network errors and timeouts are transient
                    if (attempt >= retryDelays.Length)
                    {
                        _logger.LogWarning(ex,
                            "Graph token acquisition for tenant {TenantId} failed with network error after {Attempts} attempt(s)",
                            tenantId, attempt + 1);
                        return GraphTokenResult.TransientFailure();
                    }

                    var delay = retryDelays[attempt];
                    _logger.LogWarning(ex,
                        "Graph token acquisition for tenant {TenantId} network error (attempt {Attempt}/{MaxAttempts}). Retrying in {Delay}s",
                        tenantId, attempt + 1, retryDelays.Length + 1, delay.TotalSeconds);

                    await Task.Delay(delay);
                    attempt++;
                }
            }
        }
    }

    /// <summary>
    /// Result of a Graph token acquisition attempt.
    /// Distinguishes between success, permanent failure (no consent), and transient failure (network/server error).
    /// </summary>
    public class GraphTokenResult
    {
        public string? AccessToken { get; private set; }

        /// <summary>
        /// True when the failure is transient (network error, server error, timeout).
        /// Transient failures should NOT be cached as "no consent".
        /// </summary>
        public bool IsTransient { get; private set; }

        public static GraphTokenResult Success(string? token) => new() { AccessToken = token };
        public static GraphTokenResult PermanentFailure() => new() { AccessToken = null, IsTransient = false };
        public static GraphTokenResult TransientFailure() => new() { AccessToken = null, IsTransient = true };
    }

    public class GraphConsentStatusResult
    {
        public bool IsConsented { get; set; }

        /// <summary>
        /// True when the result is due to a transient error (network/server issue).
        /// Callers should treat this as "unknown" rather than "not consented".
        /// </summary>
        public bool IsTransient { get; set; }

        public string? Message { get; set; }
    }
}
