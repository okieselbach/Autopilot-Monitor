using System;
using System.Collections.Generic;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared.Models.Graph;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.GraphResolution;

/// <summary>
/// <see cref="IGraphFeatureDetector"/> implementation backed by
/// <see cref="GraphTokenService"/> + <see cref="IMemoryCache"/>. Single source of truth
/// for "what can the AutopilotMonitor app do in this tenant right now".
/// </summary>
public sealed class GraphFeatureDetector : IGraphFeatureDetector
{
    private static readonly TimeSpan MaxCacheTtl = TimeSpan.FromMinutes(55);
    private static readonly TimeSpan ExpirySafetyMargin = TimeSpan.FromMinutes(5);

    /// <summary>
    /// TTL for the "no Graph permissions in this tenant" verdict. Global Admins viewing
    /// cross-tenant sessions repeatedly land on the same not-granted tenants; without this
    /// each visit pays the full 5+15+30 s consent-propagation retry chain. Kept well below
    /// the success-cache TTL so a freshly-granted permission lights up promptly, and the
    /// admin "Refresh" button calls <see cref="InvalidateTenant"/> for instant clearance.
    /// </summary>
    internal static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromMinutes(10);

    private static readonly IReadOnlySet<string> EmptyRoles = new HashSet<string>();

    /// <summary>
    /// Sentinel cached when token acquire returns a definitive "no consent / no SP" verdict.
    /// Distinct type so the cache-lookup branch can pattern-match it apart from a real token.
    /// </summary>
    private sealed class NegativeAcquireMarker
    {
        public static readonly NegativeAcquireMarker Instance = new();
    }

    private readonly GraphTokenService _tokenService;
    private readonly IMemoryCache _cache;
    private readonly ILogger<GraphFeatureDetector> _logger;
    private readonly TelemetryClient _telemetry;
    private readonly TimeProvider _time;

    public GraphFeatureDetector(
        GraphTokenService tokenService,
        IMemoryCache cache,
        ILogger<GraphFeatureDetector> logger,
        TelemetryClient telemetry)
        : this(tokenService, cache, logger, telemetry, TimeProvider.System)
    {
    }

    /// <summary>Test seam — deterministic time for TTL assertions.</summary>
    internal GraphFeatureDetector(
        GraphTokenService tokenService,
        IMemoryCache cache,
        ILogger<GraphFeatureDetector> logger,
        TelemetryClient telemetry,
        TimeProvider time)
    {
        _tokenService = tokenService;
        _cache = cache;
        _logger = logger;
        _telemetry = telemetry;
        _time = time;
    }

    public async Task<bool> HasPermissionAsync(string tenantId, string graphAppRole, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(graphAppRole)) return false;
        var ctx = await TryGetTokenContextAsync(tenantId, ct).ConfigureAwait(false);
        return ctx != null && ctx.GrantedRoles.Contains(graphAppRole);
    }

    public async Task<IReadOnlySet<string>> GetGrantedRolesAsync(string tenantId, CancellationToken ct = default)
    {
        var ctx = await TryGetTokenContextAsync(tenantId, ct).ConfigureAwait(false);
        return ctx?.GrantedRoles ?? EmptyRoles;
    }

    public async Task<GraphTenantTokenContext?> TryGetTokenContextAsync(string tenantId, CancellationToken ct = default)
    {
        var (ctx, _) = await AcquireInternalAsync(tenantId, ct).ConfigureAwait(false);
        return ctx;
    }

    public async Task<GraphPermissionSnapshot> GetSnapshotAsync(string tenantId, CancellationToken ct = default)
    {
        var (ctx, isTransient) = await AcquireInternalAsync(tenantId, ct).ConfigureAwait(false);
        return new GraphPermissionSnapshot
        {
            GrantedRoles = ctx?.GrantedRoles ?? EmptyRoles,
            IsTransient = isTransient,
        };
    }

    /// <summary>
    /// Single source of truth for acquire-and-parse. Both <see cref="TryGetTokenContextAsync"/>
    /// and <see cref="GetSnapshotAsync"/> wrap this; the snapshot variant additionally surfaces
    /// the <c>isTransient</c> bit which the admin UI uses to render "try again" vs "0 of 0".
    /// </summary>
    private async Task<(GraphTenantTokenContext? Ctx, bool IsTransient)> AcquireInternalAsync(string tenantId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return (null, false);

        var cacheKey = BuildCacheKey(tenantId);
        if (_cache.TryGetValue(cacheKey, out object? cached) && cached != null)
        {
            if (cached is NegativeAcquireMarker)
            {
                // Definitive "no consent / no SP in this tenant" verdict from a recent attempt.
                return (null, false);
            }
            if (cached is GraphTenantTokenContext cachedCtx
                && cachedCtx.ExpiresAt > _time.GetUtcNow() + ExpirySafetyMargin)
            {
                return (cachedCtx, false);
            }
        }

        GraphTokenResult tokenResult;
        try
        {
            tokenResult = await _tokenService.GetAccessTokenAsync(tenantId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Caller's budget elapsed mid-flight — treat as transient so we don't pretend
            // the tenant simply has nothing granted.
            return (null, true);
        }

        if (tokenResult.IsTransient) return (null, true);
        if (string.IsNullOrEmpty(tokenResult.AccessToken))
        {
            // PermanentFailure: tenant has not consented to the multi-tenant app, the SP was
            // revoked, or the app credentials are misconfigured. Cache the verdict briefly so
            // repeated cross-tenant clicks don't keep replaying the consent-propagation
            // retry chain for the same dead-end tenant.
            CacheNegativeVerdict(cacheKey);
            return (null, false);
        }

        if (!TryParseToken(tokenResult.AccessToken!, out var roles, out var expiresAt))
        {
            // Pathological — AAD returned a non-JWT. Leave uncached so a retry can recover.
            _logger.LogWarning("Graph access token for tenant {TenantId} could not be parsed as JWT; skipping role detection.", tenantId);
            return (null, false);
        }

        var ctx = new GraphTenantTokenContext
        {
            TenantId = tenantId,
            AccessToken = tokenResult.AccessToken!,
            GrantedRoles = roles,
            ExpiresAt = expiresAt,
        };

        var ttl = ComputeTtl(expiresAt);
        _cache.Set(cacheKey, ctx, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl });

        _logger.LogDebug(
            "GraphFeatureDetector: cached token for tenant {TenantId} with {RoleCount} roles, TTL={Ttl}",
            tenantId, roles.Count, ttl);

        // Telemetry: fires only on FRESH acquire (cache miss). Frequency is bounded by the
        // cache TTL (~55 min per tenant), so this is the right pulse for "which tenants
        // currently have the optional Graph permissions on" without spamming AppInsights.
        EmitPermissionsDetected(tenantId, roles, ttl);
        return (ctx, false);
    }

    private void EmitPermissionsDetected(string tenantId, IReadOnlySet<string> roles, TimeSpan ttl)
    {
        try
        {
            _telemetry.TrackEvent("GraphAddOnPermissionsDetected", new Dictionary<string, string>
            {
                ["TenantId"] = tenantId,
                ["GrantedRoleCount"] = roles.Count.ToString(CultureInfo.InvariantCulture),
                ["HasScriptDisplayNames"] = roles.Contains(GraphAppPermissions.DeviceManagementScriptsReadAll)
                    .ToString(CultureInfo.InvariantCulture),
                ["TokenCacheTtlMinutes"] = ((int)ttl.TotalMinutes).ToString(CultureInfo.InvariantCulture),
            });
        }
        catch (Exception ex)
        {
            // Never let a telemetry failure poison the acquire hot path.
            _logger.LogDebug(ex, "GraphAddOnPermissionsDetected telemetry emit failed");
        }
    }

    public void InvalidateTenant(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return;
        // Single cache key carries both the success token AND the negative marker, so one
        // Remove call clears whichever verdict is currently in flight.
        _cache.Remove(BuildCacheKey(tenantId));
        _logger.LogInformation("GraphFeatureDetector: cache invalidated for tenant {TenantId}", tenantId);
    }

    private void CacheNegativeVerdict(string cacheKey)
    {
        _cache.Set(cacheKey, NegativeAcquireMarker.Instance, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = NegativeCacheTtl,
        });
        _logger.LogDebug(
            "GraphFeatureDetector: negative verdict cached at key={CacheKey} for TTL={Ttl}",
            cacheKey, NegativeCacheTtl);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private TimeSpan ComputeTtl(DateTimeOffset tokenExpiresAt)
    {
        var remaining = tokenExpiresAt - _time.GetUtcNow() - ExpirySafetyMargin;
        if (remaining <= TimeSpan.Zero) return TimeSpan.FromMinutes(1); // pathological but harmless
        return remaining < MaxCacheTtl ? remaining : MaxCacheTtl;
    }

    private static string BuildCacheKey(string tenantId) => $"graph-feature-detector:{tenantId}";

    /// <summary>
    /// Parses the JWT into (roles, expires-at). Does NOT validate signature — this is our own
    /// token issued seconds ago by Azure AD over TLS, we read it for introspection only.
    /// Returns false on malformed input.
    /// </summary>
    internal static bool TryParseToken(string accessToken, out IReadOnlySet<string> roles, out DateTimeOffset expiresAt)
    {
        roles = EmptyRoles;
        expiresAt = DateTimeOffset.MinValue;

        if (string.IsNullOrWhiteSpace(accessToken)) return false;

        JwtSecurityToken jwt;
        try
        {
            var handler = new JwtSecurityTokenHandler();
            if (!handler.CanReadToken(accessToken)) return false;
            jwt = handler.ReadJwtToken(accessToken);
        }
        catch (Exception)
        {
            return false;
        }

        var parsedRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var claim in jwt.Claims.Where(c => c.Type == "roles"))
        {
            if (!string.IsNullOrWhiteSpace(claim.Value))
            {
                parsedRoles.Add(claim.Value);
            }
        }

        roles = parsedRoles;
        expiresAt = jwt.ValidTo == DateTime.MinValue
            ? DateTimeOffset.MinValue
            : new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero);
        return true;
    }
}
