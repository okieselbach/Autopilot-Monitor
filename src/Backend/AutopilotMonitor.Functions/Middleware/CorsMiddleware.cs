using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;

namespace AutopilotMonitor.Functions.Middleware;

/// <summary>
/// Handles CORS preflight (OPTIONS) requests in the worker pipeline.
/// In Azure Functions Isolated Worker model, the platform CORS handler doesn't
/// reliably intercept OPTIONS before the worker middleware chain runs.
/// This middleware must be first in the pipeline.
/// </summary>
public class CorsMiddleware : IFunctionsWorkerMiddleware
{
    private readonly HashSet<string> _allowedOrigins;

    public CorsMiddleware()
    {
        // Read from custom App Setting (set in Azure Portal → Configuration → Application settings)
        // Format: comma-separated origins, e.g. "https://www.autopilotmonitor.com,https://example.com"
        var origins = Environment.GetEnvironmentVariable("CORS_ALLOWED_ORIGINS") ?? "";

        _allowedOrigins = new HashSet<string>(
            origins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var httpContext = context.GetHttpContext();
        if (httpContext == null)
        {
            await next(context);
            return;
        }

        var origin = httpContext.Request.Headers.Origin.FirstOrDefault();

        // Add CORS headers to all responses if origin is allowed
        if (!string.IsNullOrEmpty(origin) && _allowedOrigins.Contains(origin))
        {
            httpContext.Response.Headers["Access-Control-Allow-Origin"] = origin;
            httpContext.Response.Headers["Access-Control-Allow-Credentials"] = "true";
        }

        // Handle OPTIONS preflight — respond immediately, skip rest of pipeline
        if (string.Equals(httpContext.Request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(origin) && _allowedOrigins.Contains(origin))
            {
                httpContext.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, PUT, PATCH, DELETE, OPTIONS";
                httpContext.Response.Headers["Access-Control-Allow-Headers"] = "Authorization, Content-Type, X-Requested-With";
                httpContext.Response.Headers["Access-Control-Max-Age"] = "86400";
                httpContext.Response.StatusCode = 204;
            }
            else
            {
                httpContext.Response.StatusCode = 403;
            }
            return;
        }

        await next(context);
    }
}
