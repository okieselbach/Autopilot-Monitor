using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using Azure;
using Azure.Data.Tables;
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
    private readonly TableStorageService _storageService;
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

    public ApiKeyMiddleware(ILogger<ApiKeyMiddleware> logger, TableStorageService storageService, RateLimitService rateLimitService)
    {
        _logger = logger;
        _storageService = storageService;
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
            var tableClient = _storageService.GetTableClient(Constants.TableNames.ApiKeys);

            TableEntity? foundKey = null;
            await foreach (var entity in tableClient.QueryAsync<TableEntity>(
                filter: $"KeyHash eq '{keyHash}'"))
            {
                foundKey = entity;
                break;
            }

            if (foundKey == null)
            {
                _logger.LogWarning("ApiKey: invalid key presented (hash not found)");
                httpContext.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                await httpContext.Response.WriteAsJsonAsync(new { error = "Invalid API key" });
                return;
            }

            // Check IsActive
            if (foundKey.TryGetValue("IsActive", out var isActiveObj) && isActiveObj is bool isActive && !isActive)
            {
                httpContext.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                await httpContext.Response.WriteAsJsonAsync(new { error = "API key is revoked" });
                return;
            }

            // Check expiry
            if (foundKey.TryGetValue("ExpiresAt", out var expiresObj) && expiresObj is DateTimeOffset expiresAt)
            {
                if (expiresAt < DateTimeOffset.UtcNow)
                {
                    httpContext.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                    await httpContext.Response.WriteAsJsonAsync(new { error = "API key has expired" });
                    return;
                }
            }

            var scope = foundKey.TryGetValue("Scope", out var scopeObj) ? scopeObj?.ToString() ?? "tenant" : "tenant";
            var keyTenantId = foundKey.TryGetValue("TenantId", out var tidObj) ? tidObj?.ToString() : null;

            // Rate limit: 60 req/min for tenant-scoped, 120 req/min for global-scoped
            var maxRequests = scope == "global" ? 120 : 60;
            var rateLimitResult = _rateLimitService.CheckRateLimit($"apikey_{foundKey.RowKey}", maxRequests);
            if (!rateLimitResult.IsAllowed)
            {
                _logger.LogWarning("ApiKey rate limit exceeded: keyId={KeyId}", foundKey.RowKey);
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
                new Claim("key_id", foundKey.RowKey),
                new Claim("key_scope", scope),
            };

            if (scope == "global")
            {
                // Global key: mark as GlobalAdmin for policy purposes
                claims.Add(new Claim("is_global_admin", "true"));
                claims.Add(new Claim("upn", $"apikey-global@{foundKey.RowKey}"));
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

            _logger.LogDebug("ApiKey auth: scope={Scope}, tenantId={TenantId}, keyId={KeyId}", scope, keyTenantId, foundKey.RowKey);

            // Increment request count (fire-and-forget)
            _ = IncrementRequestCountAsync(tableClient, foundKey.PartitionKey, foundKey.RowKey);

            await next(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing API key");
            httpContext.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            await httpContext.Response.WriteAsJsonAsync(new { error = "Internal server error" });
        }
    }

    private async Task IncrementRequestCountAsync(TableClient tableClient, string pk, string rk)
    {
        try
        {
            var result = await tableClient.GetEntityAsync<TableEntity>(pk, rk);
            var entity = result.Value;
            var count = entity.TryGetValue("RequestCount", out var c) ? Convert.ToInt64(c) : 0L;
            entity["RequestCount"] = count + 1;
            await tableClient.UpdateEntityAsync(entity, entity.ETag);
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
