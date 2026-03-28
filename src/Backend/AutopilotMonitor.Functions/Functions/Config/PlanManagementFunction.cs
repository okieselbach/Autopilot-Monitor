using System.Net;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Config
{
    public class PlanManagementFunction
    {
        private readonly ILogger<PlanManagementFunction> _logger;
        private readonly IConfigRepository _configRepo;
        private readonly IAdminRepository _adminRepo;

        public PlanManagementFunction(
            ILogger<PlanManagementFunction> logger,
            IConfigRepository configRepo,
            IAdminRepository adminRepo)
        {
            _logger = logger;
            _configRepo = configRepo;
            _adminRepo = adminRepo;
        }

        /// <summary>
        /// PATCH /api/config/{tenantId}/plan
        /// Sets the plan tier for a tenant. Body: { "planTier": "pro" }
        /// </summary>
        [Function("SetTenantPlanTier")]
        public async Task<HttpResponseData> SetPlanTier(
            [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "config/{tenantId}/plan")] HttpRequestData req,
            string tenantId)
        {
            _logger.LogInformation("SetTenantPlanTier: tenantId={TenantId}", tenantId);

            try
            {
                var body = await req.ReadFromJsonAsync<SetPlanTierRequest>();
                if (body == null || string.IsNullOrWhiteSpace(body.PlanTier))
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { error = "planTier is required" });
                    return badRequest;
                }

                var validTiers = new[] { "free", "pro", "enterprise" };
                if (!validTiers.Contains(body.PlanTier.ToLowerInvariant()))
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { error = $"Invalid planTier. Valid values: {string.Join(", ", validTiers)}" });
                    return badRequest;
                }

                var config = await _configRepo.GetTenantConfigurationAsync(tenantId);
                if (config == null)
                {
                    var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFound.WriteAsJsonAsync(new { error = "Tenant not found" });
                    return notFound;
                }

                config.PlanTier = body.PlanTier.ToLowerInvariant();
                await _configRepo.SaveTenantConfigurationAsync(config);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new { tenantId, planTier = config.PlanTier });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting plan tier");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { error = "Internal server error" });
                return errorResponse;
            }
        }

        /// <summary>
        /// PATCH /api/api-keys/{keyId}/rate-limit
        /// Sets a custom rate limit for an API key. Body: { "rateLimitPerMinute": 300 }
        /// Send null to remove the override.
        /// </summary>
        [Function("SetApiKeyRateLimit")]
        public async Task<HttpResponseData> SetKeyRateLimit(
            [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "api-keys/{keyId}/rate-limit")] HttpRequestData req,
            string keyId)
        {
            _logger.LogInformation("SetApiKeyRateLimit: keyId={KeyId}", keyId);

            try
            {
                var body = await req.ReadFromJsonAsync<SetRateLimitRequest>();
                if (body == null)
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { error = "Request body is required" });
                    return badRequest;
                }

                if (body.RateLimitPerMinute.HasValue && body.RateLimitPerMinute.Value < 1)
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { error = "rateLimitPerMinute must be >= 1 or null" });
                    return badRequest;
                }

                // Try to find the key — check both GLOBAL and all tenants
                var allKeys = await _adminRepo.GetAllApiKeysAsync();
                var key = allKeys.FirstOrDefault(k => k.KeyId == keyId);

                if (key == null)
                {
                    var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFound.WriteAsJsonAsync(new { error = "API key not found" });
                    return notFound;
                }

                key.CustomRateLimitPerMinute = body.RateLimitPerMinute;

                var partitionKey = key.Scope == "global" ? "GLOBAL" : key.TenantId;
                await _adminRepo.StoreApiKeyAsync(partitionKey, key);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    keyId = key.KeyId,
                    customRateLimitPerMinute = key.CustomRateLimitPerMinute,
                    scope = key.Scope
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting API key rate limit");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { error = "Internal server error" });
                return errorResponse;
            }
        }

        private class SetPlanTierRequest
        {
            public string PlanTier { get; set; } = string.Empty;
        }

        private class SetRateLimitRequest
        {
            public int? RateLimitPerMinute { get; set; }
        }
    }
}
