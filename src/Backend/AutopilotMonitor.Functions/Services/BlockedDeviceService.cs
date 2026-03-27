using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Manages temporarily blocked devices (e.g. rogue devices sending excessive data).
    /// Uses Azure Table Storage for persistence across backend restarts, with a
    /// ConcurrentDictionary in-memory cache for fast lookups at ingest time.
    /// </summary>
    public class BlockedDeviceService
    {
        private readonly TableClient _tableClient;
        private readonly ILogger<BlockedDeviceService> _logger;

        // Cache key: "tenantId|serialNumber" (lower-cased serial number for case-insensitive matching)
        // Cache value: BlockCacheEntry with UnblockAt and Action. Expired entries are treated as unblocked.
        private readonly ConcurrentDictionary<string, BlockCacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

        // Tracks which tenants have had their block list loaded into the cache.
        // Lazy loading: populated on first lookup per tenant.
        private readonly ConcurrentDictionary<string, bool> _loadedTenants = new(StringComparer.OrdinalIgnoreCase);

        public BlockedDeviceService(IConfiguration configuration, ILogger<BlockedDeviceService> logger)
        {
            _logger = logger;
            var connectionString = configuration["AzureTableStorageConnectionString"];
            var serviceClient = new TableServiceClient(connectionString);
            _tableClient = serviceClient.GetTableClient(Constants.TableNames.BlockedDevices);
        }

        /// <summary>
        /// Checks whether a device is currently blocked.
        /// Fast path: in-memory cache (loaded lazily per tenant from Table Storage).
        /// Returns the action type ("Block" or "Kill") so callers can differentiate.
        /// </summary>
        public async Task<(bool isBlocked, DateTime? unblockAt, string action)> IsBlockedAsync(string tenantId, string serialNumber)
        {
            if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(serialNumber))
                return (false, null, "Block");

            // Lazy-load block list for this tenant if not yet done
            if (!_loadedTenants.ContainsKey(tenantId))
            {
                await LoadTenantBlockListAsync(tenantId);
            }

            var cacheKey = BuildCacheKey(tenantId, serialNumber);
            if (_cache.TryGetValue(cacheKey, out var entry))
            {
                if (DateTime.UtcNow < entry.UnblockAt)
                    return (true, entry.UnblockAt, entry.Action);

                // Block has expired — remove from cache
                _cache.TryRemove(cacheKey, out _);
            }

            return (false, null, "Block");
        }

        /// <summary>
        /// Blocks a device for the specified duration. Updates both Table Storage and the in-memory cache.
        /// <paramref name="action"/> is "Block" (stop uploads) or "Kill" (remote self-destruct).
        /// </summary>
        public async Task BlockDeviceAsync(string tenantId, string serialNumber, int durationHours, string blockedByEmail, string? reason = null, string action = "Block")
        {
            var now = DateTime.UtcNow;
            var unblockAt = now.AddHours(durationHours);

            var entity = new TableEntity(tenantId, EncodeRowKey(serialNumber))
            {
                ["SerialNumber"] = serialNumber,
                ["BlockedAt"] = now,
                ["UnblockAt"] = unblockAt,
                ["BlockedByEmail"] = blockedByEmail ?? string.Empty,
                ["DurationHours"] = durationHours,
                ["Reason"] = reason ?? string.Empty,
                ["Action"] = action ?? "Block"
            };

            await _tableClient.UpsertEntityAsync(entity);

            // Update cache immediately
            var cacheKey = BuildCacheKey(tenantId, serialNumber);
            _cache[cacheKey] = new BlockCacheEntry { UnblockAt = unblockAt, Action = action ?? "Block" };

            _logger.LogWarning(
                "Device {Action}: TenantId={TenantId}, SerialNumber={SerialNumber}, BlockedBy={BlockedBy}, Until={UnblockAt}, Reason={Reason}",
                action, tenantId, serialNumber, blockedByEmail, unblockAt, reason);
        }

        /// <summary>
        /// Removes a device block immediately. Updates both Table Storage and the in-memory cache.
        /// </summary>
        public async Task UnblockDeviceAsync(string tenantId, string serialNumber)
        {
            try
            {
                await _tableClient.DeleteEntityAsync(tenantId, EncodeRowKey(serialNumber));
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Already removed — that's fine
            }

            // Remove from cache immediately
            var cacheKey = BuildCacheKey(tenantId, serialNumber);
            _cache.TryRemove(cacheKey, out _);

            _logger.LogInformation("Device unblocked: TenantId={TenantId}, SerialNumber={SerialNumber}", tenantId, serialNumber);
        }

        /// <summary>
        /// Returns all currently active (non-expired) blocked devices for a tenant.
        /// Also cleans up expired entries from Table Storage as a side-effect.
        /// </summary>
        public async Task<List<BlockedDeviceEntry>> GetBlockedDevicesAsync(string tenantId)
        {
            var result = new List<BlockedDeviceEntry>();
            var expiredRowKeys = new List<string>();
            var now = DateTime.UtcNow;

            await foreach (var entity in _tableClient.QueryAsync<TableEntity>(e => e.PartitionKey == tenantId))
            {
                var unblockAt = entity.GetDateTimeOffset("UnblockAt")?.UtcDateTime ?? DateTime.MinValue;

                if (now >= unblockAt)
                {
                    expiredRowKeys.Add(entity.RowKey);
                    continue;
                }

                result.Add(new BlockedDeviceEntry
                {
                    TenantId = tenantId,
                    SerialNumber = entity.GetString("SerialNumber") ?? DecodeRowKey(entity.RowKey),
                    BlockedAt = entity.GetDateTimeOffset("BlockedAt")?.UtcDateTime ?? now,
                    UnblockAt = unblockAt,
                    BlockedByEmail = entity.GetString("BlockedByEmail"),
                    DurationHours = entity.GetInt32("DurationHours") ?? 12,
                    Reason = entity.GetString("Reason"),
                    Action = entity.GetString("Action") ?? "Block"
                });
            }

            // Clean up expired entries from storage (fire-and-forget, best effort)
            _ = CleanupExpiredEntriesAsync(tenantId, expiredRowKeys);

            return result;
        }

        /// <summary>
        /// Returns all currently active (non-expired) blocked devices across ALL tenants.
        /// Also cleans up expired entries from Table Storage as a side-effect.
        /// </summary>
        public async Task<List<BlockedDeviceEntry>> GetAllBlockedDevicesAsync()
        {
            var result = new List<BlockedDeviceEntry>();
            var expiredKeys = new List<(string partitionKey, string rowKey)>();
            var now = DateTime.UtcNow;

            await foreach (var entity in _tableClient.QueryAsync<TableEntity>())
            {
                var unblockAt = entity.GetDateTimeOffset("UnblockAt")?.UtcDateTime ?? DateTime.MinValue;

                if (now >= unblockAt)
                {
                    expiredKeys.Add((entity.PartitionKey, entity.RowKey));
                    continue;
                }

                result.Add(new BlockedDeviceEntry
                {
                    TenantId = entity.PartitionKey,
                    SerialNumber = entity.GetString("SerialNumber") ?? DecodeRowKey(entity.RowKey),
                    BlockedAt = entity.GetDateTimeOffset("BlockedAt")?.UtcDateTime ?? now,
                    UnblockAt = unblockAt,
                    BlockedByEmail = entity.GetString("BlockedByEmail"),
                    DurationHours = entity.GetInt32("DurationHours") ?? 12,
                    Reason = entity.GetString("Reason"),
                    Action = entity.GetString("Action") ?? "Block"
                });
            }

            // Clean up expired entries (fire-and-forget, best effort)
            foreach (var (pk, rk) in expiredKeys)
            {
                try { await _tableClient.DeleteEntityAsync(pk, rk); }
                catch { /* best effort */ }
            }

            return result;
        }

        // -----------------------------------------------------------------------
        // Private helpers
        // -----------------------------------------------------------------------

        private async Task LoadTenantBlockListAsync(string tenantId)
        {
            // Mark tenant as loaded first (before async call) to prevent parallel loads.
            // A race here just means two loads — acceptable for correctness.
            _loadedTenants[tenantId] = true;

            try
            {
                var now = DateTime.UtcNow;

                await foreach (var entity in _tableClient.QueryAsync<TableEntity>(e => e.PartitionKey == tenantId))
                {
                    var unblockAt = entity.GetDateTimeOffset("UnblockAt")?.UtcDateTime ?? DateTime.MinValue;
                    if (unblockAt <= now) continue; // Skip expired

                    var serialNumber = entity.GetString("SerialNumber") ?? DecodeRowKey(entity.RowKey);
                    _cache[BuildCacheKey(tenantId, serialNumber)] = new BlockCacheEntry
                    {
                        UnblockAt = unblockAt,
                        Action = entity.GetString("Action") ?? "Block"
                    };
                }

                _logger.LogDebug("Loaded block list for tenant {TenantId}", tenantId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load block list for tenant {TenantId}", tenantId);
                // Remove loaded marker so it can be retried next time
                _loadedTenants.TryRemove(tenantId, out _);
            }
        }

        private async Task CleanupExpiredEntriesAsync(string tenantId, List<string> rowKeys)
        {
            foreach (var rowKey in rowKeys)
            {
                try { await _tableClient.DeleteEntityAsync(tenantId, rowKey); }
                catch { /* best effort */ }
            }
        }

        private class BlockCacheEntry
        {
            public DateTime UnblockAt { get; set; }
            public string Action { get; set; } = "Block";
        }

        private static string BuildCacheKey(string tenantId, string serialNumber)
            => $"{tenantId}|{serialNumber.ToUpperInvariant()}";

        /// <summary>Azure Table RowKey must not contain /\#? and must be <= 1KB. URL-encode to be safe.</summary>
        private static string EncodeRowKey(string serialNumber)
            => Uri.EscapeDataString(serialNumber);

        private static string DecodeRowKey(string encodedRowKey)
            => Uri.UnescapeDataString(encodedRowKey);
    }

    /// <summary>
    /// Represents a blocked device entry returned by the API
    /// </summary>
    public class BlockedDeviceEntry
    {
        public string TenantId { get; set; } = string.Empty;
        public string SerialNumber { get; set; } = string.Empty;
        public DateTime BlockedAt { get; set; }
        public DateTime UnblockAt { get; set; }
        public string BlockedByEmail { get; set; } = string.Empty;
        public int DurationHours { get; set; }
        public string? Reason { get; set; }
        public string Action { get; set; } = "Block";
    }
}
