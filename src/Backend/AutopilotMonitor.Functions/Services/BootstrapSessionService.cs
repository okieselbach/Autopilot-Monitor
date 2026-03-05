using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
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
    /// Service for managing bootstrap sessions (OOBE pre-enrollment agent deployment).
    /// Uses a single Azure Table with two partition schemes:
    /// - Main entity: PartitionKey = TenantId, RowKey = ShortCode (for tenant-scoped listing)
    /// - Lookup entity: PartitionKey = "CodeLookup", RowKey = ShortCode (for anonymous code validation)
    /// </summary>
    public class BootstrapSessionService
    {
        private readonly TableClient _tableClient;
        private readonly ILogger<BootstrapSessionService> _logger;
        private readonly IMemoryCache _cache;

        // Charset for short codes: no ambiguous chars (0/O, 1/l/I)
        private const string ShortCodeCharset = "23456789abcdefghjkmnpqrstuvwxyz";
        private const int ShortCodeLength = 6;
        private const string CodeLookupPartition = "CodeLookup";

        // Token validation cache TTL (short enough for revocation to propagate)
        private static readonly TimeSpan TokenCacheDuration = TimeSpan.FromSeconds(60);

        public BootstrapSessionService(
            IConfiguration configuration,
            ILogger<BootstrapSessionService> logger,
            IMemoryCache cache)
        {
            _logger = logger;
            _cache = cache;

            var connectionString = configuration["AzureTableStorageConnectionString"];
            var serviceClient = new TableServiceClient(connectionString);
            _tableClient = serviceClient.GetTableClient(Constants.TableNames.BootstrapSessions);
        }

        /// <summary>
        /// Creates a new bootstrap session with a unique short code and token.
        /// </summary>
        public async Task<BootstrapSession> CreateAsync(string tenantId, int validityHours, string createdByUpn, string label)
        {
            // Clamp validity to 1–168 hours (1 week max)
            validityHours = Math.Max(1, Math.Min(168, validityHours));

            var now = DateTime.UtcNow;
            var token = Guid.NewGuid().ToString();

            // Generate unique short code with collision guard
            string? shortCode = null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                shortCode = GenerateShortCode();

                try
                {
                    // Try to insert the lookup entity first (acts as uniqueness guard)
                    var lookupEntity = new TableEntity(CodeLookupPartition, shortCode)
                    {
                        { "TenantId", tenantId },
                        { "Token", token },
                        { "ExpiresAt", now.AddHours(validityHours) },
                        { "IsRevoked", false }
                    };
                    await _tableClient.AddEntityAsync(lookupEntity);
                    break; // No collision
                }
                catch (RequestFailedException ex) when (ex.Status == 409)
                {
                    _logger.LogWarning("Bootstrap short code collision on attempt {Attempt}: {Code}", attempt + 1, shortCode!);
                    shortCode = null;
                }
            }

            if (shortCode == null)
                throw new InvalidOperationException("Failed to generate unique short code after 5 attempts");

            // Insert the main entity
            var session = new BootstrapSession
            {
                TenantId = tenantId,
                ShortCode = shortCode,
                Token = token,
                CreatedAt = now,
                ExpiresAt = now.AddHours(validityHours),
                CreatedByUpn = createdByUpn,
                IsRevoked = false,
                UsageCount = 0,
                Label = label ?? ""
            };

            var mainEntity = new TableEntity(tenantId, shortCode)
            {
                { "Token", session.Token },
                { "CreatedAt", session.CreatedAt },
                { "ExpiresAt", session.ExpiresAt },
                { "CreatedByUpn", session.CreatedByUpn },
                { "IsRevoked", false },
                { "UsageCount", 0 },
                { "Label", session.Label }
            };
            await _tableClient.AddEntityAsync(mainEntity);

            _logger.LogInformation("Created bootstrap session {ShortCode} for tenant {TenantId}, expires {ExpiresAt}",
                shortCode, tenantId, session.ExpiresAt);

            return session;
        }

        /// <summary>
        /// Lists bootstrap sessions for a tenant (includes active and recently expired/revoked).
        /// </summary>
        public async Task<List<BootstrapSession>> ListAsync(string tenantId)
        {
            var cutoff = DateTime.UtcNow.AddHours(-24);
            var sessions = new List<BootstrapSession>();

            var query = _tableClient.QueryAsync<TableEntity>(
                filter: $"PartitionKey eq '{tenantId}'");

            await foreach (var entity in query)
            {
                var expiresAt = entity.GetDateTime("ExpiresAt") ?? DateTime.MinValue;
                var isRevoked = entity.GetBoolean("IsRevoked") ?? false;

                // Skip sessions that expired more than 24h ago and are not active
                if (expiresAt < cutoff && (isRevoked || expiresAt < DateTime.UtcNow))
                    continue;

                sessions.Add(MapFromEntity(entity));
            }

            return sessions.OrderByDescending(s => s.CreatedAt).ToList();
        }

        /// <summary>
        /// Revokes a bootstrap session. Already-running agents with the token will be rejected on next request.
        /// </summary>
        public async Task<bool> RevokeAsync(string tenantId, string shortCode)
        {
            try
            {
                // Update main entity
                var mainEntity = await _tableClient.GetEntityAsync<TableEntity>(tenantId, shortCode);
                mainEntity.Value["IsRevoked"] = true;
                await _tableClient.UpdateEntityAsync(mainEntity.Value, mainEntity.Value.ETag, TableUpdateMode.Merge);

                // Update lookup entity
                try
                {
                    var lookupEntity = await _tableClient.GetEntityAsync<TableEntity>(CodeLookupPartition, shortCode);
                    lookupEntity.Value["IsRevoked"] = true;
                    await _tableClient.UpdateEntityAsync(lookupEntity.Value, lookupEntity.Value.ETag, TableUpdateMode.Merge);
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    _logger.LogWarning("Lookup entity not found for bootstrap code {ShortCode} during revoke", shortCode);
                }

                // Invalidate cache
                _cache.Remove($"bootstrap-token:{GetTokenFromEntity(mainEntity.Value)}");

                _logger.LogInformation("Revoked bootstrap session {ShortCode} for tenant {TenantId}", shortCode, tenantId);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogWarning("Bootstrap session {ShortCode} not found for tenant {TenantId}", shortCode, tenantId);
                return false;
            }
        }

        /// <summary>
        /// Validates a bootstrap code (anonymous, called by the /go/{code} route).
        /// Returns the session if the code is valid and not expired/revoked.
        /// </summary>
        public async Task<BootstrapSession?> ValidateCodeAsync(string shortCode)
        {
            try
            {
                // O(1) lookup via the CodeLookup partition
                var lookupEntity = await _tableClient.GetEntityAsync<TableEntity>(CodeLookupPartition, shortCode);
                var lookup = lookupEntity.Value;

                var isRevoked = lookup.GetBoolean("IsRevoked") ?? false;
                var expiresAt = lookup.GetDateTime("ExpiresAt") ?? DateTime.MinValue;

                if (isRevoked)
                {
                    _logger.LogWarning("Bootstrap code {ShortCode} has been revoked", shortCode);
                    return null;
                }

                if (expiresAt < DateTime.UtcNow)
                {
                    _logger.LogWarning("Bootstrap code {ShortCode} has expired (expired {ExpiresAt})", shortCode, expiresAt);
                    return null;
                }

                var tenantId = lookup.GetString("TenantId") ?? "";
                var token = lookup.GetString("Token") ?? "";

                // Read main entity for full details
                var mainEntity = await _tableClient.GetEntityAsync<TableEntity>(tenantId, shortCode);
                var session = MapFromEntity(mainEntity.Value);

                // Increment usage count (fire-and-forget)
                _ = IncrementUsageCountAsync(tenantId, shortCode);

                return session;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogWarning("Bootstrap code {ShortCode} not found", shortCode);
                return null;
            }
        }

        /// <summary>
        /// Validates a bootstrap token (called by SecurityValidator on every agent request with X-Bootstrap-Token).
        /// Results are cached for 60 seconds.
        /// </summary>
        public async Task<BootstrapSession?> ValidateTokenAsync(string token)
        {
            var cacheKey = $"bootstrap-token:{token}";

            if (_cache.TryGetValue(cacheKey, out BootstrapSession? cached))
            {
                // Check if cached result is still valid
                if (cached != null && !cached.IsRevoked && cached.ExpiresAt > DateTime.UtcNow)
                    return cached;

                // Cached as invalid or expired — re-validate
                _cache.Remove(cacheKey);
            }

            // Query for the token across all partitions (excluding CodeLookup)
            // This is a table scan filtered by Token property — acceptable because:
            // 1. Called rarely (once per 60s per token due to caching)
            // 2. BootstrapSessions table is small (dozens of entries, not millions)
            var query = _tableClient.QueryAsync<TableEntity>(
                filter: $"Token eq '{token}' and PartitionKey ne '{CodeLookupPartition}'",
                maxPerPage: 1);

            BootstrapSession? session = null;
            await foreach (var entity in query)
            {
                session = MapFromEntity(entity);
                break; // We only need the first match
            }

            if (session == null || session.IsRevoked || session.ExpiresAt < DateTime.UtcNow)
            {
                // Cache negative result briefly to prevent repeated lookups
                _cache.Set<BootstrapSession?>(cacheKey, null, TimeSpan.FromSeconds(10));
                return null;
            }

            _cache.Set(cacheKey, session, TokenCacheDuration);
            return session;
        }

        /// <summary>
        /// Deletes bootstrap sessions that expired more than 7 days ago.
        /// Called by maintenance timer.
        /// </summary>
        public async Task CleanupExpiredAsync()
        {
            var cutoff = DateTime.UtcNow.AddDays(-7);
            var toDelete = new List<(string partitionKey, string rowKey, ETag etag)>();

            var query = _tableClient.QueryAsync<TableEntity>(
                filter: $"ExpiresAt lt datetime'{cutoff:yyyy-MM-ddTHH:mm:ssZ}'");

            await foreach (var entity in query)
            {
                toDelete.Add((entity.PartitionKey, entity.RowKey, entity.ETag));
            }

            foreach (var (partitionKey, rowKey, etag) in toDelete)
            {
                try
                {
                    await _tableClient.DeleteEntityAsync(partitionKey, rowKey, etag);
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    // Already deleted — ignore
                }
            }

            if (toDelete.Count > 0)
                _logger.LogInformation("Cleaned up {Count} expired bootstrap sessions", toDelete.Count);
        }

        private static string GenerateShortCode()
        {
            var bytes = new byte[ShortCodeLength];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            var chars = new char[ShortCodeLength];
            for (int i = 0; i < ShortCodeLength; i++)
            {
                chars[i] = ShortCodeCharset[bytes[i] % ShortCodeCharset.Length];
            }
            return new string(chars);
        }

        private static BootstrapSession MapFromEntity(TableEntity entity)
        {
            return new BootstrapSession
            {
                TenantId = entity.PartitionKey,
                ShortCode = entity.RowKey,
                Token = entity.GetString("Token"),
                CreatedAt = entity.GetDateTime("CreatedAt") ?? DateTime.MinValue,
                ExpiresAt = entity.GetDateTime("ExpiresAt") ?? DateTime.MinValue,
                CreatedByUpn = entity.GetString("CreatedByUpn") ?? "",
                IsRevoked = entity.GetBoolean("IsRevoked") ?? false,
                UsageCount = entity.GetInt32("UsageCount") ?? 0,
                Label = entity.GetString("Label") ?? ""
            };
        }

        private static string GetTokenFromEntity(TableEntity entity)
        {
            return entity.GetString("Token") ?? "";
        }

        private async Task IncrementUsageCountAsync(string tenantId, string shortCode)
        {
            try
            {
                var entity = await _tableClient.GetEntityAsync<TableEntity>(tenantId, shortCode);
                var currentCount = entity.Value.GetInt32("UsageCount") ?? 0;
                entity.Value["UsageCount"] = currentCount + 1;
                await _tableClient.UpdateEntityAsync(entity.Value, entity.Value.ETag, TableUpdateMode.Merge);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to increment usage count for bootstrap code {ShortCode}", shortCode);
            }
        }
    }
}
