using System.Collections.Concurrent;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Middleware;

/// <summary>
/// Records lightweight "last seen" presence for authenticated web users so a Global Admin can see
/// who is actively using the portal right now. Runs after PolicyEnforcementMiddleware, so
/// RequestContext (UPN, role, tenant) is already resolved. Agent/device routes carry no UPN and are
/// skipped automatically.
///
/// Writes are throttled per process to at most one upsert per user per <see cref="ThrottleWindow"/>,
/// so even under heavy navigation the presence write rate stays negligible. Best-effort: a failed
/// presence write never affects the request it rode in on (fail-open).
/// </summary>
public class UserPresenceMiddleware : IFunctionsWorkerMiddleware
{
    private static readonly TimeSpan ThrottleWindow = TimeSpan.FromSeconds(60);

    // Per-process throttle: key = "tenantId|upn" (lowercased) → last upsert time (UTC).
    private static readonly ConcurrentDictionary<string, DateTime> _lastWrite = new();

    private readonly IMetricsRepository _metricsRepo;
    private readonly ILogger<UserPresenceMiddleware> _logger;

    public UserPresenceMiddleware(IMetricsRepository metricsRepo, ILogger<UserPresenceMiddleware> logger)
    {
        _metricsRepo = metricsRepo;
        _logger = logger;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var ctx = context.GetRequestContext();
        if (!string.IsNullOrEmpty(ctx.UserPrincipalName) && !string.IsNullOrEmpty(ctx.TenantId))
        {
            try
            {
                if (ShouldWrite(ctx.TenantId, ctx.UserPrincipalName))
                {
                    var role = string.IsNullOrEmpty(ctx.UserRole) ? "Authenticated" : ctx.UserRole;
                    await _metricsRepo.RecordUserPresenceAsync(ctx.TenantId, ctx.UserPrincipalName, role);
                }
            }
            catch (Exception ex)
            {
                // Presence is best-effort observability — never block the request.
                _logger.LogDebug(ex, "[UserPresence] Failed to record presence for {User}", ctx.UserPrincipalName);
            }
        }

        await next(context);
    }

    /// <summary>
    /// Returns true at most once per <see cref="ThrottleWindow"/> per user (per process), and stamps
    /// the write time when it does. Keeps the hot path from upserting on every single request.
    /// </summary>
    internal static bool ShouldWrite(string tenantId, string upn)
    {
        var key = $"{tenantId}|{upn.ToLowerInvariant()}";
        var now = DateTime.UtcNow;
        if (_lastWrite.TryGetValue(key, out var last) && now - last < ThrottleWindow)
            return false;
        _lastWrite[key] = now;
        return true;
    }
}
