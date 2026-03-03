using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Functions.Security
{
    /// <summary>
    /// Validates devices against Intune Corporate Device Identifiers via Microsoft Graph beta API.
    /// Uses the importedDeviceIdentities/searchExistingIdentities endpoint with manufacturerModelSerial type.
    /// Caches positive/negative lookups to reduce Graph traffic.
    /// </summary>
    public class CorporateIdentifierValidator
    {
        private static readonly TimeSpan PositiveCacheTtl = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromMinutes(5);

        private readonly ILogger<CorporateIdentifierValidator> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IMemoryCache _cache;
        private readonly GraphTokenService _graphTokenService;

        public CorporateIdentifierValidator(
            ILogger<CorporateIdentifierValidator> logger,
            IHttpClientFactory httpClientFactory,
            IMemoryCache cache,
            GraphTokenService graphTokenService)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _cache = cache;
            _graphTokenService = graphTokenService;
        }

        public async Task<CorporateIdentifierValidationResult> ValidateAsync(
            string tenantId,
            string? manufacturer,
            string? model,
            string? serialNumber,
            string? sessionId = null)
        {
            if (string.IsNullOrWhiteSpace(manufacturer) || string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(serialNumber))
            {
                return new CorporateIdentifierValidationResult
                {
                    IsValid = false,
                    ErrorMessage = "Manufacturer, model, or serial number header not provided"
                };
            }

            var normalizedManufacturer = manufacturer.Trim();
            var normalizedModel = model.Trim();
            var normalizedSerial = serialNumber.Trim();
            var cacheKey = BuildCacheKey(tenantId, normalizedManufacturer, normalizedModel, normalizedSerial, sessionId);

            if (_cache.TryGetValue(cacheKey, out CorporateIdentifierValidationResult? cached) && cached != null)
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
                    return new CorporateIdentifierValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = "Graph access token could not be acquired"
                    };
                }

                var graphClient = _httpClientFactory.CreateClient();
                graphClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                // POST https://graph.microsoft.com/beta/deviceManagement/importedDeviceIdentities/searchExistingIdentities
                // Body: {"importedDeviceIdentities":[{"importedDeviceIdentityType":"manufacturerModelSerial",
                //        "importedDeviceIdentifier":"manufacturer,model,serialNumber"}]}
                var identifier = $"{normalizedManufacturer},{normalizedModel},{normalizedSerial}";
                var requestBody = new
                {
                    importedDeviceIdentities = new[]
                    {
                        new
                        {
                            importedDeviceIdentityType = "manufacturerModelSerial",
                            importedDeviceIdentifier = identifier
                        }
                    }
                };

                var content = new StringContent(
                    JsonConvert.SerializeObject(requestBody),
                    Encoding.UTF8,
                    "application/json");

                var graphUrl = "https://graph.microsoft.com/beta/deviceManagement/importedDeviceIdentities/searchExistingIdentities";
                var response = await graphClient.PostAsync(graphUrl, content);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Corporate identifier validation Graph query failed for tenant {TenantId}. Status: {StatusCode}. Body: {ResponseBody}",
                        tenantId,
                        (int)response.StatusCode,
                        responseBody);

                    return CacheAndReturn(cacheKey, new CorporateIdentifierValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = $"Graph query failed with status {(int)response.StatusCode}"
                    }, isPositive: false);
                }

                var data = JsonConvert.DeserializeObject<JObject>(responseBody);
                var identities = data?["value"] as JArray;

                if (identities == null || identities.Count == 0)
                {
                    return CacheAndReturn(cacheKey, new CorporateIdentifierValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = $"Device '{identifier}' is not registered as a Corporate Identifier"
                    }, isPositive: false);
                }

                var result = new CorporateIdentifierValidationResult
                {
                    IsValid = true,
                    Identifier = identifier
                };

                _logger.LogInformation(
                    "Corporate identifier validation succeeded for tenant {TenantId}, session {SessionId}, identifier {Identifier}",
                    tenantId,
                    sessionId ?? "<none>",
                    identifier);

                return CacheAndReturn(cacheKey, result, isPositive: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error during corporate identifier validation for tenant {TenantId}, session {SessionId}, identifier {Manufacturer},{Model},{SerialNumber}",
                    tenantId,
                    sessionId ?? "<none>",
                    normalizedManufacturer,
                    normalizedModel,
                    normalizedSerial);

                return CacheAndReturn(cacheKey, new CorporateIdentifierValidationResult
                {
                    IsValid = false,
                    ErrorMessage = $"Error during corporate identifier validation: {ex.Message}"
                }, isPositive: false);
            }
        }

        private static string BuildCacheKey(string tenantId, string manufacturer, string model, string serialNumber, string? sessionId)
        {
            var baseKey = $"corporate-id-validation:{tenantId}:{manufacturer}:{model}:{serialNumber}";
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                return $"{baseKey}:{sessionId}";
            }
            return baseKey;
        }

        private CorporateIdentifierValidationResult CacheAndReturn(
            string cacheKey,
            CorporateIdentifierValidationResult result,
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

    public class CorporateIdentifierValidationResult
    {
        public bool IsValid { get; set; }
        public string? Identifier { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
