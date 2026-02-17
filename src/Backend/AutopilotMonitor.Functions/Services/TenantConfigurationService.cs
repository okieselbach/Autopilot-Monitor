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
        private readonly IMemoryCache _cache;

        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

        public TenantConfigurationService(IConfiguration configuration, ILogger<TenantConfigurationService> logger, IMemoryCache cache)
        {
            _logger = logger;
            _cache = cache;

            var connectionString = configuration["AzureTableStorageConnectionString"];
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

            var cacheKey = $"tenant-config:{tenantId}";

            if (_cache.TryGetValue(cacheKey, out TenantConfiguration? cachedConfig) && cachedConfig != null)
            {
                return cachedConfig;
            }

            try
            {
                // Load from Table Storage
                var entity = await _tableClient.GetEntityAsync<TableEntity>(tenantId, "config");
                var config = ConvertFromTableEntity(entity.Value);

                _cache.Set(cacheKey, config, CacheDuration);

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

                    _cache.Set(cacheKey, defaultConfig, CacheDuration);

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
                _cache.Remove($"tenant-config:{config.TenantId}");

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
            _cache.Remove($"tenant-config:{tenantId}");
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
                { "ValidateAutopilotDevice", config.ValidateAutopilotDevice },
                { "AllowInsecureAgentRequests", config.AllowInsecureAgentRequests },
                { "DataRetentionDays", config.DataRetentionDays },
                { "SessionTimeoutHours", config.SessionTimeoutHours },
                { "MaxNdjsonPayloadSizeMB", config.MaxNdjsonPayloadSizeMB },
                { "EnablePerformanceCollector", config.EnablePerformanceCollector },
                { "PerformanceCollectorIntervalSeconds", config.PerformanceCollectorIntervalSeconds },
                { "MaxAuthFailures", config.MaxAuthFailures },
                { "AuthFailureTimeoutMinutes", config.AuthFailureTimeoutMinutes },
                { "SelfDestructOnComplete", config.SelfDestructOnComplete },
                { "KeepLogFile", config.KeepLogFile },
                { "RebootOnComplete", config.RebootOnComplete },
                { "RebootDelaySeconds", config.RebootDelaySeconds },
                { "EnableGeoLocation", config.EnableGeoLocation },
                { "EnableImeMatchLog", config.EnableImeMatchLog },
                { "LogLevel", config.LogLevel },
                { "MaxBatchSize", config.MaxBatchSize },
                { "DiagnosticsBlobSasUrl", config.DiagnosticsBlobSasUrl },
                { "DiagnosticsUploadMode", config.DiagnosticsUploadMode },
                { "TeamsWebhookUrl", config.TeamsWebhookUrl },
                { "TeamsNotifyOnSuccess", config.TeamsNotifyOnSuccess },
                { "TeamsNotifyOnFailure", config.TeamsNotifyOnFailure }
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
                ValidateAutopilotDevice = entity.GetBoolean("ValidateAutopilotDevice") ?? entity.GetBoolean("ValidateSerialNumber") ?? false,
                AllowInsecureAgentRequests = entity.GetBoolean("AllowInsecureAgentRequests") ?? false,
                DataRetentionDays = entity.GetInt32("DataRetentionDays") ?? 90,
                SessionTimeoutHours = entity.GetInt32("SessionTimeoutHours") ?? 5,
                MaxNdjsonPayloadSizeMB = entity.GetInt32("MaxNdjsonPayloadSizeMB") ?? 5,
                EnablePerformanceCollector = entity.GetBoolean("EnablePerformanceCollector") ?? false,
                PerformanceCollectorIntervalSeconds = entity.GetInt32("PerformanceCollectorIntervalSeconds") ?? 30,
                MaxAuthFailures = entity.GetInt32("MaxAuthFailures"),
                AuthFailureTimeoutMinutes = entity.GetInt32("AuthFailureTimeoutMinutes"),
                SelfDestructOnComplete = entity.GetBoolean("SelfDestructOnComplete"),
                KeepLogFile = entity.GetBoolean("KeepLogFile"),
                RebootOnComplete = entity.GetBoolean("RebootOnComplete"),
                RebootDelaySeconds = entity.GetInt32("RebootDelaySeconds"),
                EnableGeoLocation = entity.GetBoolean("EnableGeoLocation"),
                EnableImeMatchLog = entity.GetBoolean("EnableImeMatchLog"),
                LogLevel = entity.GetString("LogLevel"),
                MaxBatchSize = entity.GetInt32("MaxBatchSize"),
                DiagnosticsBlobSasUrl = entity.GetString("DiagnosticsBlobSasUrl"),
                DiagnosticsUploadMode = entity.GetString("DiagnosticsUploadMode") ?? "Off",
                TeamsWebhookUrl = entity.GetString("TeamsWebhookUrl"),
                TeamsNotifyOnSuccess = entity.GetBoolean("TeamsNotifyOnSuccess") ?? true,
                TeamsNotifyOnFailure = entity.GetBoolean("TeamsNotifyOnFailure") ?? true
            };
        }
    }
}
