using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Service for managing global admin configuration in Azure Table Storage
    /// </summary>
    public class AdminConfigurationService
    {
        private readonly TableClient _adminTableClient;
        private readonly TableClient _tenantConfigTableClient;
        private readonly ILogger<AdminConfigurationService> _logger;
        private readonly IMemoryCache _cache;

        private const string CacheKey = "admin-config";
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

        public AdminConfigurationService(IConfiguration configuration, ILogger<AdminConfigurationService> logger, IMemoryCache cache)
        {
            _logger = logger;
            _cache = cache;

            var connectionString = configuration["AzureTableStorageConnectionString"];
            var serviceClient = new TableServiceClient(connectionString);
            _adminTableClient = serviceClient.GetTableClient(Constants.TableNames.AdminConfiguration);
            _tenantConfigTableClient = serviceClient.GetTableClient(Constants.TableNames.TenantConfiguration);
            // Tables are initialized centrally by TableInitializerService at startup
        }

        /// <summary>
        /// Gets global admin configuration (uses cache with 5-minute TTL)
        /// </summary>
        public async Task<AdminConfiguration> GetConfigurationAsync()
        {
            if (_cache.TryGetValue(CacheKey, out AdminConfiguration? cachedConfig) && cachedConfig != null)
            {
                return cachedConfig;
            }

            try
            {
                // Load from Table Storage
                var entity = await _adminTableClient.GetEntityAsync<TableEntity>("GlobalConfig", "config");
                var config = ConvertFromTableEntity(entity.Value);

                _cache.Set(CacheKey, config, CacheDuration);

                return config;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Configuration not found - create and save default immediately
                _logger.LogInformation("Admin configuration not found, creating and saving default configuration");
                var defaultConfig = AdminConfiguration.CreateDefault();

                try
                {
                    // Save the default configuration to Table Storage
                    var entity = ConvertToTableEntity(defaultConfig);
                    await _adminTableClient.UpsertEntityAsync(entity);

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

                var entity = ConvertToTableEntity(config);
                await _adminTableClient.UpsertEntityAsync(entity);

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

                // Query all tenant configurations
                var query = _tenantConfigTableClient.QueryAsync<TableEntity>(filter: $"RowKey eq 'config'");

                await foreach (var entity in query)
                {
                    // Check if tenant has a custom rate limit override
                    var customRateLimit = entity.GetInt32("CustomRateLimitRequestsPerMinute");

                    if (customRateLimit.HasValue)
                    {
                        // Skip tenants with custom rate limit
                        tenantsSkipped++;
                        _logger.LogDebug($"Skipping tenant {entity.PartitionKey} - has custom rate limit: {customRateLimit}");
                        continue;
                    }

                    // Update tenant's rate limit to match global default
                    entity["RateLimitRequestsPerMinute"] = globalRateLimit;
                    entity["LastUpdated"] = DateTime.UtcNow;
                    entity["UpdatedBy"] = "System (Global Rate Limit Sync)";

                    await _tenantConfigTableClient.UpdateEntityAsync(entity, ETag.All, TableUpdateMode.Replace);
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

        private TableEntity ConvertToTableEntity(AdminConfiguration config)
        {
            var entity = new TableEntity("GlobalConfig", "config")
            {
                { "LastUpdated", config.LastUpdated },
                { "UpdatedBy", config.UpdatedBy },
                { "GlobalRateLimitRequestsPerMinute", config.GlobalRateLimitRequestsPerMinute }
            };

            return entity;
        }

        private AdminConfiguration ConvertFromTableEntity(TableEntity entity)
        {
            return new AdminConfiguration
            {
                PartitionKey = entity.PartitionKey,
                RowKey = entity.RowKey,
                LastUpdated = entity.GetDateTime("LastUpdated") ?? DateTime.UtcNow,
                UpdatedBy = entity.GetString("UpdatedBy") ?? "Unknown",
                GlobalRateLimitRequestsPerMinute = entity.GetInt32("GlobalRateLimitRequestsPerMinute") ?? 100
            };
        }
    }
}
