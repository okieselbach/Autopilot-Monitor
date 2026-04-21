using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Service for managing global admin configuration.
    /// Caching and business logic layer — delegates storage to IConfigRepository.
    /// </summary>
    public class AdminConfigurationService
    {
        private readonly IConfigRepository _configRepo;
        private readonly ILogger<AdminConfigurationService> _logger;
        private readonly IMemoryCache _cache;

        private const string CacheKey = "admin-config";
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

        public AdminConfigurationService(IConfigRepository configRepo, ILogger<AdminConfigurationService> logger, IMemoryCache cache)
        {
            _configRepo = configRepo;
            _logger = logger;
            _cache = cache;
        }

        /// <summary>
        /// Gets global admin configuration (uses cache with 5-minute TTL).
        /// <para>
        /// Virtual so it can be mocked via Moq in consumer tests that need controlled
        /// flag values (e.g. <c>IndexReconcileTimer</c> flag-gate tests).
        /// </para>
        /// </summary>
        public virtual async Task<AdminConfiguration> GetConfigurationAsync()
        {
            if (_cache.TryGetValue(CacheKey, out AdminConfiguration? cachedConfig) && cachedConfig != null)
            {
                return cachedConfig;
            }

            try
            {
                // Load from repository
                var config = await _configRepo.GetAdminConfigurationAsync();

                if (config != null)
                {
                    _cache.Set(CacheKey, config, CacheDuration);
                    return config;
                }

                // Configuration not found - create and save default immediately
                _logger.LogInformation("Admin configuration not found, creating and saving default configuration");
                var defaultConfig = AdminConfiguration.CreateDefault();

                try
                {
                    // Save the default configuration via repository
                    await _configRepo.SaveAdminConfigurationAsync(defaultConfig);

                    _cache.Set(CacheKey, defaultConfig, CacheDuration);

                    _logger.LogInformation("Default admin configuration created and saved");
                }
                catch (Exception saveEx)
                {
                    _logger.LogError(saveEx, "Failed to save default admin configuration");
                }

                return defaultConfig;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading admin configuration");
                // Return default on error (fail-open)
                return AdminConfiguration.CreateDefault();
            }
        }

        /// <summary>
        /// Saves global admin configuration and syncs rate limit to all tenant configurations
        /// </summary>
        public async Task SaveConfigurationAsync(AdminConfiguration config)
        {
            if (config == null)
            {
                throw new ArgumentException("Configuration is required");
            }

            try
            {
                config.LastUpdated = DateTime.UtcNow;

                await _configRepo.SaveAdminConfigurationAsync(config);

                // Invalidate cache
                _cache.Remove(CacheKey);

                _logger.LogInformation($"Admin configuration saved by {config.UpdatedBy}");

                // Sync global rate limit to all tenant configurations (only if not custom)
                await SyncRateLimitToTenantsAsync(config.GlobalRateLimitRequestsPerMinute);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving admin configuration");
                throw;
            }
        }

        /// <summary>
        /// Syncs the global rate limit to all tenant configurations that don't have a custom override
        /// </summary>
        private async Task SyncRateLimitToTenantsAsync(int globalRateLimit)
        {
            try
            {
                _logger.LogInformation($"Syncing global rate limit ({globalRateLimit}) to all tenant configurations...");

                var tenantsUpdated = 0;
                var tenantsSkipped = 0;

                // Get all tenant configurations via repository
                var allConfigs = await _configRepo.GetAllTenantConfigurationsAsync();

                foreach (var tenantConfig in allConfigs)
                {
                    // Check if tenant has a custom rate limit override
                    if (tenantConfig.CustomRateLimitRequestsPerMinute.HasValue)
                    {
                        // Skip tenants with custom rate limit
                        tenantsSkipped++;
                        _logger.LogDebug($"Skipping tenant {tenantConfig.TenantId} - has custom rate limit: {tenantConfig.CustomRateLimitRequestsPerMinute}");
                        continue;
                    }

                    // Update tenant's rate limit to match global default
                    tenantConfig.RateLimitRequestsPerMinute = globalRateLimit;
                    tenantConfig.LastUpdated = DateTime.UtcNow;
                    tenantConfig.UpdatedBy = "System (Global Rate Limit Sync)";

                    await _configRepo.SaveTenantConfigurationAsync(tenantConfig);
                    tenantsUpdated++;
                }

                _logger.LogInformation($"Rate limit sync completed: {tenantsUpdated} tenants updated, {tenantsSkipped} tenants skipped (custom rate limit)");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error syncing rate limit to tenant configurations");
                // Don't throw - we don't want to fail the admin config save if sync fails
            }
        }

        /// <summary>
        /// Invalidates cache (forces reload on next request)
        /// </summary>
        public void InvalidateCache()
        {
            _cache.Remove(CacheKey);
        }
    }
}
