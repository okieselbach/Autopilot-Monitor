using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Middleware;

/// <summary>
/// Per-user rate limiting for authenticated (JWT) requests.
/// Runs after PolicyEnforcementMiddleware so RequestContext (UPN, IsGlobalAdmin) is already resolved.
/// Agent/device routes have no RequestContext and are automatically skipped.
/// </summary>
public class UserRateLimitMiddleware : IFunctionsWorkerMiddleware
{
    private readonly RateLimitService _rateLimitService;
    private readonly AdminConfigurationService _adminConfigService;
    private readonly ILogger<UserRateLimitMiddleware> _logger;

    public UserRateLimitMiddleware(
        RateLimitService rateLimitService,
        AdminConfigurationService adminConfigService,
        ILogger<UserRateLimitMiddleware> logger)
    {
        _rateLimitService = rateLimitService;
        _adminConfigService = adminConfigService;
        _logger = logger;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var httpContext = context.GetHttpContext();
        if (httpContext == null)
        {
            await next(context);
            return;
        }

        var requestContext = context.GetRequestContext();
        if (string.IsNullOrEmpty(requestContext.UserPrincipalName))
        {
            // Anonymous or device-auth route — no user to rate limit
            await next(context);
            return;
        }

        // Fail-open: if rate limiting logic throws, let the request through.
        // A broken rate limiter must never take down the application.
        RateLimitResult result;
        try
        {
            var config = await _adminConfigService.GetConfigurationAsync();
            var limit = requestContext.IsGlobalAdmin
                ? config.GlobalAdminRateLimitRequestsPerMinute
                : config.UserRateLimitRequestsPerMinute;

            var key = $"user_ratelimit_{requestContext.UserPrincipalName.ToLowerInvariant()}";
            result = _rateLimitService.CheckRateLimit(key, limit);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[UserRateLimit] Rate limit check failed for user={User}, allowing request (fail-open)",
                requestContext.UserPrincipalName);
            await next(context);
            return;
        }

        // Always set rate limit headers for authenticated requests
        var remaining = Math.Max(0, result.MaxRequests - result.RequestsInWindow);
        var resetEpoch = DateTimeOffset.UtcNow.Add(result.WindowDuration).ToUnixTimeSeconds();

        httpContext.Response.Headers["X-RateLimit-Limit"] = result.MaxRequests.ToString();
        httpContext.Response.Headers["X-RateLimit-Remaining"] = remaining.ToString();
        httpContext.Response.Headers["X-RateLimit-Reset"] = resetEpoch.ToString();

        if (result.IsAllowed)
        {
            await next(context);
            return;
        }

        // Throttled — return 429
        var retryAfterSeconds = result.RetryAfter.HasValue
            ? (int)result.RetryAfter.Value.TotalSeconds
            : 60;

        _logger.LogWarning(
            "[UserRateLimit] THROTTLED user={User} requests={Count}/{Max} retryAfter={RetryAfter}s",
            requestContext.UserPrincipalName, result.RequestsInWindow, result.MaxRequests, retryAfterSeconds);

        httpContext.Response.StatusCode = (int)HttpStatusCode.TooManyRequests;
        httpContext.Response.Headers["Retry-After"] = retryAfterSeconds.ToString();
        httpContext.Response.ContentType = "application/json";

        await httpContext.Response.WriteAsJsonAsync(new
        {
            success = false,
            message = $"Rate limit exceeded: {result.MaxRequests} requests per minute",
            rateLimitExceeded = true,
            rateLimitInfo = new
            {
                requestsInWindow = result.RequestsInWindow,
                maxRequests = result.MaxRequests,
                windowDurationSeconds = result.WindowDuration.TotalSeconds,
                retryAfterSeconds
            }
        });
    }
}
