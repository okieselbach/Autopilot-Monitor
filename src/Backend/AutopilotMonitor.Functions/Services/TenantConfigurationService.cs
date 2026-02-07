using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Service for managing tenant-specific configuration in Azure Table Storage
    /// </summary>
    public class TenantConfigurationService
    {
        private readonly TableClient _tableClient;
        private readonly ILogger<TenantConfigurationService> _logger;

        // In-memory cache for configuration (to avoid table lookups on every request)
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CachedConfig> _configCache;

        public TenantConfigurationService(IConfiguration configuration, ILogger<TenantConfigurationService> logger)
        {
            _logger = logger;
            _configCache = new System.Collections.Concurrent.ConcurrentDictionary<string, CachedConfig>();

            var connectionString = configuration["AzureWebJobsStorage"];
            var serviceClient = new TableServiceClient(connectionString);
            _tableClient = serviceClient.GetTableClient(Constants.TableNames.TenantConfiguration);
            // Table is initialized centrally by TableInitializerService at startup
        }

        /// <summary>
        /// Gets configuration for a tenant (uses cache with 5-minute TTL)
        /// </summary>
        public async Task<TenantConfiguration> GetConfigurationAsync(string tenantId)
        {
            if (string.IsNullOrEmpty(tenantId))
            {
                _logger.LogWarning("GetConfiguration called with empty tenantId");
                return TenantConfiguration.CreateDefault("unknown");
            }

            // Check cache first (5-minute TTL)
            if (_configCache.TryGetValue(tenantId, out var cached))
            {
                if (DateTime.UtcNow.Subtract(cached.CachedAt).TotalMinutes < 5)
                {
                    return cached.Configuration;
                }
            }

            try
            {
                // Load from Table Storage
                var entity = await _tableClient.GetEntityAsync<TableEntity>(tenantId, "config");
                var config = ConvertFromTableEntity(entity.Value);

                // Update cache
                _configCache[tenantId] = new CachedConfig
                {
                    Configuration = config,
                    CachedAt = DateTime.UtcNow
                };

                return config;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Configuration not found - create and save default immediately
                _logger.LogInformation($"Configuration not found for tenant {tenantId}, creating and saving default configuration");
                var defaultConfig = TenantConfiguration.CreateDefault(tenantId);

                try
                {
                    // Save the default configuration to Table Storage
                    var entity = ConvertToTableEntity(defaultConfig);
                    await _tableClient.UpsertEntityAsync(entity);

                    // Update cache
                    _configCache[tenantId] = new CachedConfig
                    {
                        Configuration = defaultConfig,
                        CachedAt = DateTime.UtcNow
                    };

                    _logger.LogInformation($"Default configuration created and saved for tenant {tenantId}");
                }
                catch (Exception saveEx)
                {
                    _logger.LogError(saveEx, $"Failed to save default configuration for tenant {tenantId}");
                }

                return defaultConfig;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error loading configuration for tenant {tenantId}");
                // Return default on error (fail-open for now, can be changed to fail-closed)
                return TenantConfiguration.CreateDefault(tenantId);
            }
        }

        /// <summary>
        /// Saves configuration for a tenant
        /// </summary>
        public async Task SaveConfigurationAsync(TenantConfiguration config)
        {
            if (config == null || string.IsNullOrEmpty(config.TenantId))
            {
                throw new ArgumentException("Configuration and TenantId are required");
            }

            try
            {
                config.LastUpdated = DateTime.UtcNow;

                var entity = ConvertToTableEntity(config);
                await _tableClient.UpsertEntityAsync(entity);

                // Invalidate cache
                _configCache.TryRemove(config.TenantId, out _);

                _logger.LogInformation($"Configuration saved for tenant {config.TenantId} by {config.UpdatedBy}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error saving configuration for tenant {config.TenantId}");
                throw;
            }
        }

        /// <summary>
        /// Invalidates cache for a tenant (forces reload on next request)
        /// </summary>
        public void InvalidateCache(string tenantId)
        {
            _configCache.TryRemove(tenantId, out _);
        }

        /// <summary>
        /// Gets all tenant configurations (for Galactic Admin use)
        /// </summary>
        public async Task<List<TenantConfiguration>> GetAllConfigurationsAsync()
        {
            try
            {
                var configurations = new List<TenantConfiguration>();

                await foreach (var entity in _tableClient.QueryAsync<TableEntity>(filter: "RowKey eq 'config'"))
                {
                    var config = ConvertFromTableEntity(entity);
                    configurations.Add(config);
                }

                return configurations.OrderBy(c => c.TenantId).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading all tenant configurations");
                throw;
            }
        }

        private TableEntity ConvertToTableEntity(TenantConfiguration config)
        {
            var entity = new TableEntity(config.TenantId, "config")
            {
                { "DomainName", config.DomainName },
                { "LastUpdated", config.LastUpdated },
                { "UpdatedBy", config.UpdatedBy },
                { "Disabled", config.Disabled },
                { "DisabledReason", config.DisabledReason },
                { "DisabledUntil", config.DisabledUntil },
                { "RateLimitRequestsPerMinute", config.RateLimitRequestsPerMinute },
                { "CustomRateLimitRequestsPerMinute", config.CustomRateLimitRequestsPerMinute },
                { "ManufacturerWhitelist", config.ManufacturerWhitelist },
                { "ModelWhitelist", config.ModelWhitelist },
                { "ValidateSerialNumber", config.ValidateSerialNumber },
                { "DataRetentionDays", config.DataRetentionDays },
                { "SessionTimeoutHours", config.SessionTimeoutHours },
                { "MaxNdjsonPayloadSizeMB", config.MaxNdjsonPayloadSizeMB },
                { "CustomSettings", config.CustomSettings },
                { "EnablePerformanceCollector", config.EnablePerformanceCollector },
                { "PerformanceCollectorIntervalSeconds", config.PerformanceCollectorIntervalSeconds },
                { "EnableDownloadProgressCollector", config.EnableDownloadProgressCollector },
                { "DownloadProgressCollectorIntervalSeconds", config.DownloadProgressCollectorIntervalSeconds },
                { "EnableCertValidationCollector", config.EnableCertValidationCollector },
                { "EnableEspUiStateCollector", config.EnableEspUiStateCollector },
                { "EspUiStateCollectorIntervalSeconds", config.EspUiStateCollectorIntervalSeconds }
            };

            return entity;
        }

        private TenantConfiguration ConvertFromTableEntity(TableEntity entity)
        {
            return new TenantConfiguration
            {
                TenantId = entity.PartitionKey,
                DomainName = entity.GetString("DomainName") ?? "",
                LastUpdated = entity.GetDateTime("LastUpdated") ?? DateTime.UtcNow,
                UpdatedBy = entity.GetString("UpdatedBy") ?? "Unknown",
                Disabled = entity.GetBoolean("Disabled") ?? false,
                DisabledReason = entity.GetString("DisabledReason"),
                DisabledUntil = entity.GetDateTime("DisabledUntil"),
                RateLimitRequestsPerMinute = entity.GetInt32("RateLimitRequestsPerMinute") ?? 100,
                CustomRateLimitRequestsPerMinute = entity.GetInt32("CustomRateLimitRequestsPerMinute"),
                ManufacturerWhitelist = entity.GetString("ManufacturerWhitelist") ?? "Dell*,HP*,Lenovo*,Microsoft Corporation",
                ModelWhitelist = entity.GetString("ModelWhitelist") ?? "*",
                ValidateSerialNumber = entity.GetBoolean("ValidateSerialNumber") ?? false,
                DataRetentionDays = entity.GetInt32("DataRetentionDays") ?? 90,
                SessionTimeoutHours = entity.GetInt32("SessionTimeoutHours") ?? 5,
                MaxNdjsonPayloadSizeMB = entity.GetInt32("MaxNdjsonPayloadSizeMB") ?? 5,
                CustomSettings = entity.GetString("CustomSettings"),
                EnablePerformanceCollector = entity.GetBoolean("EnablePerformanceCollector") ?? false,
                PerformanceCollectorIntervalSeconds = entity.GetInt32("PerformanceCollectorIntervalSeconds") ?? 30,
                EnableDownloadProgressCollector = entity.GetBoolean("EnableDownloadProgressCollector") ?? false,
                DownloadProgressCollectorIntervalSeconds = entity.GetInt32("DownloadProgressCollectorIntervalSeconds") ?? 10,
                EnableCertValidationCollector = entity.GetBoolean("EnableCertValidationCollector") ?? false,
                EnableEspUiStateCollector = entity.GetBoolean("EnableEspUiStateCollector") ?? false,
                EspUiStateCollectorIntervalSeconds = entity.GetInt32("EspUiStateCollectorIntervalSeconds") ?? 15
            };
        }

        private class CachedConfig
        {
            public TenantConfiguration Configuration { get; set; } = null!;
            public DateTime CachedAt { get; set; }
        }
    }
}
