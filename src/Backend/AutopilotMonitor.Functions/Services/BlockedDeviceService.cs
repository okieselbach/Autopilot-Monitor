using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Manages temporarily blocked devices (e.g. rogue devices sending excessive data).
    /// Uses IDeviceSecurityRepository for persistence, with a
    /// ConcurrentDictionary in-memory cache for fast lookups at ingest time.
    /// </summary>
    public class BlockedDeviceService
    {
        private readonly IDeviceSecurityRepository _securityRepo;
        private readonly ILogger<BlockedDeviceService> _logger;

        // Cache key: "tenantId|serialNumber" (lower-cased serial number for case-insensitive matching)
        // Cache value: BlockCacheEntry with UnblockAt and Action. Expired entries are treated as unblocked.
        private readonly ConcurrentDictionary<string, BlockCacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

        // Tracks which tenants have had their block list loaded into the cache.
        // Lazy loading: populated on first lookup per tenant.
        private readonly ConcurrentDictionary<string, bool> _loadedTenants = new(StringComparer.OrdinalIgnoreCase);

        public BlockedDeviceService(IDeviceSecurityRepository securityRepo, ILogger<BlockedDeviceService> logger)
        {
            _securityRepo = securityRepo;
            _logger = logger;
        }

        /// <summary>
        /// Checks whether a device is currently blocked.
        /// Fast path: in-memory cache (loaded lazily per tenant from storage).
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
        /// Blocks a device for the specified duration. Updates both storage and the in-memory cache.
        /// <paramref name="action"/> is "Block" (stop uploads) or "Kill" (remote self-destruct).
        /// </summary>
        public async Task BlockDeviceAsync(string tenantId, string serialNumber, int durationHours, string blockedByEmail, string? reason = null, string action = "Block")
        {
            await _securityRepo.BlockDeviceAsync(tenantId, serialNumber, durationHours, blockedByEmail, reason, action);

            // Update cache immediately
            var unblockAt = DateTime.UtcNow.AddHours(durationHours);
            var cacheKey = BuildCacheKey(tenantId, serialNumber);
            _cache[cacheKey] = new BlockCacheEntry { UnblockAt = unblockAt, Action = action ?? "Block" };

            _logger.LogWarning(
                "Device {Action}: TenantId={TenantId}, SerialNumber={SerialNumber}, BlockedBy={BlockedBy}, Until={UnblockAt}, Reason={Reason}",
                action, tenantId, serialNumber, blockedByEmail, unblockAt, reason);
        }

        /// <summary>
        /// Removes a device block immediately. Updates both storage and the in-memory cache.
        /// </summary>
        public async Task UnblockDeviceAsync(string tenantId, string serialNumber)
        {
            await _securityRepo.UnblockDeviceAsync(tenantId, serialNumber);

            // Remove from cache immediately
            var cacheKey = BuildCacheKey(tenantId, serialNumber);
            _cache.TryRemove(cacheKey, out _);

            _logger.LogInformation("Device unblocked: TenantId={TenantId}, SerialNumber={SerialNumber}", tenantId, serialNumber);
        }

        /// <summary>
        /// Returns all currently active (non-expired) blocked devices for a tenant.
        /// Delegates to repository which also cleans up expired entries.
        /// </summary>
        public Task<List<BlockedDeviceEntry>> GetBlockedDevicesAsync(string tenantId)
            => _securityRepo.GetBlockedDevicesAsync(tenantId);

        /// <summary>
        /// Returns all currently active (non-expired) blocked devices across ALL tenants.
        /// Delegates to repository which also cleans up expired entries.
        /// </summary>
        public Task<List<BlockedDeviceEntry>> GetAllBlockedDevicesAsync()
            => _securityRepo.GetAllBlockedDevicesAsync();

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
                var entries = await _securityRepo.GetBlockedDevicesAsync(tenantId);
                var now = DateTime.UtcNow;

                foreach (var entry in entries)
                {
                    if (entry.UnblockAt == null || entry.UnblockAt <= now) continue;

                    _cache[BuildCacheKey(tenantId, entry.SerialNumber)] = new BlockCacheEntry
                    {
                        UnblockAt = entry.UnblockAt.Value,
                        Action = entry.Action
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

        private class BlockCacheEntry
        {
            public DateTime UnblockAt { get; set; }
            public string Action { get; set; } = "Block";
        }

        private static string BuildCacheKey(string tenantId, string serialNumber)
            => $"{tenantId}|{serialNumber.ToUpperInvariant()}";
    }

    // Note: BlockedDeviceEntry is now defined in AutopilotMonitor.Shared.DataAccess.IDeviceSecurityRepository
}
