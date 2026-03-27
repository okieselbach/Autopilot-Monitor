using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Middleware;

/// <summary>
/// Middleware that processes X-Api-Key header authentication before the JWT AuthenticationMiddleware.
/// When a valid API key is found, it builds a synthetic ClaimsPrincipal so downstream middleware
/// (PolicyEnforcementMiddleware) can evaluate route policies normally.
/// </summary>
public class ApiKeyMiddleware : IFunctionsWorkerMiddleware
{
    private readonly ILogger<ApiKeyMiddleware> _logger;
    private readonly IAdminRepository _adminRepo;
    private readonly RateLimitService _rateLimitService;

    // Routes that bypass ApiKey middleware (use device cert / bootstrap token auth)
    private static readonly string[] _bypassPrefixes =
    {
        "/api/agent/",
        "/api/bootstrap/",
        "/api/health",
        "/api/stats/",
        "/api/realtime/",
        "/api/progress/",
    };

    public ApiKeyMiddleware(ILogger<ApiKeyMiddleware> logger, IAdminRepository adminRepo, RateLimitService rateLimitService)
    {
        _logger = logger;
        _adminRepo = adminRepo;
        _rateLimitService = rateLimitService;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var httpContext = context.GetHttpContext();
        if (httpContext == null) { await next(context); return; }

        var requestPath = httpContext.Request.Path.Value ?? string.Empty;

        // Skip if this is a bypass route (agent/bootstrap/health)
        foreach (var prefix in _bypassPrefixes)
        {
            if (requestPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                await next(context); return;
            }
        }

        // If request already has a Bearer token, let JWT auth handle it
        var authHeader = httpContext.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            await next(context); return;
        }

        // Check for X-Api-Key header
        var apiKey = httpContext.Request.Headers["X-Api-Key"].FirstOrDefault();
        if (string.IsNullOrEmpty(apiKey))
        {
            // No API key — let normal auth pipeline handle it
            await next(context); return;
        }

        // Hash the provided key and look it up
        var keyHash = ComputeHash(apiKey);

        try
        {
            var foundKey = await _adminRepo.ValidateApiKeyAsync(keyHash);

            if (foundKey == null)
            {
                _logger.LogWarning("ApiKey: invalid key presented (hash not found)");
                httpContext.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                await httpContext.Response.WriteAsJsonAsync(new { error = "Invalid API key" });
                return;
            }

            // Check IsActive
            if (!foundKey.IsActive)
            {
                httpContext.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                await httpContext.Response.WriteAsJsonAsync(new { error = "API key is revoked" });
                return;
            }

            // Check expiry
            if (foundKey.ExpiresAt.HasValue && foundKey.ExpiresAt.Value < DateTime.UtcNow)
            {
                httpContext.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                await httpContext.Response.WriteAsJsonAsync(new { error = "API key has expired" });
                return;
            }

            var scope = foundKey.Scope ?? "tenant";
            var keyTenantId = foundKey.TenantId;

            // Rate limit: 60 req/min for tenant-scoped, 120 req/min for global-scoped
            var maxRequests = scope == "global" ? 120 : 60;
            var rateLimitResult = _rateLimitService.CheckRateLimit($"apikey_{foundKey.KeyId}", maxRequests);
            if (!rateLimitResult.IsAllowed)
            {
                _logger.LogWarning("ApiKey rate limit exceeded: keyId={KeyId}", foundKey.KeyId);
                httpContext.Response.StatusCode = 429;
                httpContext.Response.ContentType = "application/json";
                if (rateLimitResult.RetryAfter.HasValue)
                    httpContext.Response.Headers.Append("Retry-After", ((int)rateLimitResult.RetryAfter.Value.TotalSeconds + 1).ToString());
                await httpContext.Response.WriteAsJsonAsync(new { error = "RateLimitExceeded", message = "Too many requests. Please slow down.", retryAfterSeconds = (int)(rateLimitResult.RetryAfter?.TotalSeconds ?? 60) + 1 });
                return;
            }

            // Build synthetic ClaimsPrincipal for downstream policy evaluation
            var claims = new List<Claim>
            {
                new Claim("auth_method", "api_key"),
                new Claim("key_id", foundKey.KeyId),
                new Claim("key_scope", scope),
            };

            // Determine partition key for increment (matches storage layout)
            var partitionKey = scope == "global" ? "GLOBAL" : keyTenantId;

            if (scope == "global")
            {
                // Global key: mark as GlobalAdmin for policy purposes
                claims.Add(new Claim("is_global_admin", "true"));
                claims.Add(new Claim("upn", $"apikey-global@{foundKey.KeyId}"));
                context.Items["ApiKeyScope"] = "global";
            }
            else
            {
                // Tenant key: inject tenantId so PolicyEnforcementMiddleware can scope correctly
                claims.Add(new Claim("tid", keyTenantId ?? ""));
                claims.Add(new Claim("upn", $"apikey-tenant@{keyTenantId}"));
                context.Items["ApiKeyTenantId"] = keyTenantId ?? "";
                context.Items["ApiKeyScope"] = "tenant";
            }

            var identity = new ClaimsIdentity(claims, "ApiKey");
            var principal = new ClaimsPrincipal(identity);
            httpContext.User = principal;
            context.Items["ClaimsPrincipal"] = principal;

            _logger.LogDebug("ApiKey auth: scope={Scope}, tenantId={TenantId}, keyId={KeyId}", scope, keyTenantId, foundKey.KeyId);

            // Increment request count (fire-and-forget)
            _ = IncrementRequestCountAsync(partitionKey, foundKey.KeyId);

            await next(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing API key");
            httpContext.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            await httpContext.Response.WriteAsJsonAsync(new { error = "Internal server error" });
        }
    }

    private async Task IncrementRequestCountAsync(string partitionKey, string keyId)
    {
        try
        {
            await _adminRepo.IncrementApiKeyRequestCountAsync(partitionKey, keyId);
        }
        catch
        {
            // Non-fatal — don't block the request
        }
    }

    private static string ComputeHash(string key)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
