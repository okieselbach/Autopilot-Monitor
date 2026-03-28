using System.Text.Json;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Resolves the effective API rate limit for a given API key by checking (in priority order):
    /// 1. Per-key override (ApiKeyEntry.CustomRateLimitPerMinute)
    /// 2. Per-tenant override (TenantConfiguration.CustomRateLimitRequestsPerMinute)
    /// 3. Plan tier default from AdminConfiguration.PlanTierDefinitionsJson
    /// 4. Hardcoded fallback: 60 for tenant-scoped, 120 for global-scoped
    /// </summary>
    public class RateLimitConfigService
    {
        private readonly IConfigRepository _configRepo;
        private readonly IMemoryCache _cache;
        private readonly ILogger<RateLimitConfigService> _logger;

        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
        private const string AdminConfigCacheKey = "RateLimitConfig_AdminConfig";

        public RateLimitConfigService(
            IConfigRepository configRepo,
            IMemoryCache cache,
            ILogger<RateLimitConfigService> logger)
        {
            _configRepo = configRepo;
            _cache = cache;
            _logger = logger;
        }

        /// <summary>
        /// Resolves the effective rate limit (requests per minute) for the given API key.
        /// </summary>
        public async Task<int> ResolveRateLimitAsync(ApiKeyEntry key)
        {
            // 1. Per-key override
            if (key.CustomRateLimitPerMinute.HasValue && key.CustomRateLimitPerMinute.Value > 0)
                return key.CustomRateLimitPerMinute.Value;

            var scope = key.Scope ?? "tenant";

            // 2. Per-tenant override (only for tenant-scoped keys)
            if (scope != "global" && !string.IsNullOrEmpty(key.TenantId))
            {
                try
                {
                    var tenantConfig = await GetCachedTenantConfigAsync(key.TenantId);
                    if (tenantConfig?.CustomRateLimitRequestsPerMinute is > 0)
                        return tenantConfig.CustomRateLimitRequestsPerMinute.Value;

                    // 3. Plan tier default
                    var planTier = tenantConfig?.PlanTier ?? "free";
                    var planLimit = await GetPlanTierRateLimitAsync(planTier);
                    if (planLimit.HasValue)
                        return planLimit.Value;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to resolve tenant config for rate limit, using fallback");
                }
            }

            // 4. Hardcoded fallback
            return scope == "global" ? 120 : 60;
        }

        private async Task<AutopilotMonitor.Shared.Models.TenantConfiguration?> GetCachedTenantConfigAsync(string tenantId)
        {
            var cacheKey = $"RateLimitConfig_Tenant_{tenantId}";
            if (_cache.TryGetValue(cacheKey, out AutopilotMonitor.Shared.Models.TenantConfiguration? cached))
                return cached;

            var config = await _configRepo.GetTenantConfigurationAsync(tenantId);
            if (config != null)
                _cache.Set(cacheKey, config, CacheDuration);

            return config;
        }

        private async Task<int?> GetPlanTierRateLimitAsync(string planTier)
        {
            try
            {
                var adminConfig = await GetCachedAdminConfigAsync();
                if (string.IsNullOrEmpty(adminConfig?.PlanTierDefinitionsJson))
                    return null;

                var definitions = JsonSerializer.Deserialize<Dictionary<string, PlanTierDefinition>>(
                    adminConfig.PlanTierDefinitionsJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (definitions != null && definitions.TryGetValue(planTier.ToLowerInvariant(), out var def))
                    return def.ApiRateLimit > 0 ? def.ApiRateLimit : null;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to parse plan tier definitions");
            }

            return null;
        }

        private async Task<AutopilotMonitor.Shared.Models.AdminConfiguration?> GetCachedAdminConfigAsync()
        {
            if (_cache.TryGetValue(AdminConfigCacheKey, out AutopilotMonitor.Shared.Models.AdminConfiguration? cached))
                return cached;

            var config = await _configRepo.GetAdminConfigurationAsync();
            if (config != null)
                _cache.Set(AdminConfigCacheKey, config, CacheDuration);

            return config;
        }

        private class PlanTierDefinition
        {
            public int ApiRateLimit { get; set; }
        }
    }
}
