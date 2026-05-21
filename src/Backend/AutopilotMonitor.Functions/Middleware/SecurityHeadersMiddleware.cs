using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;

namespace AutopilotMonitor.Functions.Middleware;

/// <summary>
/// Stamps a static security-baseline of response headers on every HTTP response:
/// <list type="bullet">
///   <item><c>X-Content-Type-Options: nosniff</c> — disables browser MIME-sniffing,
///   blocking XSS via mis-typed uploads and strengthening Chrome's CORB protections.</item>
///   <item><c>Referrer-Policy: strict-origin-when-cross-origin</c> — strips path/query
///   from outbound Referer on cross-origin nav, leaks only origin on HTTPS→HTTPS.</item>
///   <item><c>X-Frame-Options: DENY</c> — blocks framing for legacy clients that
///   ignore CSP <c>frame-ancestors</c>; clickjacking defense.</item>
/// </list>
/// Unconditional (every route, every status) — distinct from <see cref="NoStoreCacheMiddleware"/>
/// which is allowlist-gated and concern-orthogonal.
/// </summary>
public class SecurityHeadersMiddleware : IFunctionsWorkerMiddleware
{
    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        // Direct-write before next() — OnStarting() is not reliably triggered in the
        // .NET 8 isolated worker (the host bridges the worker's response, so the hook
        // fires on a shadow object that never reaches the wire). Same pattern used by
        // CorrelationIdMiddleware, TimingAllowOriginMiddleware, NoStoreCacheMiddleware.
        var httpContext = context.GetHttpContext();
        if (httpContext != null)
        {
            ApplyHeaders(httpContext.Response.Headers);
        }

        await next(context);
    }

    /// <summary>
    /// Applies the security-baseline headers to the given header dictionary.
    /// Skips any header already present so explicit per-endpoint overrides win
    /// (e.g. a future endpoint that legitimately needs <c>X-Frame-Options: SAMEORIGIN</c>).
    /// </summary>
    internal static void ApplyHeaders(IHeaderDictionary headers)
    {
        if (headers == null)
            return;

        if (!headers.ContainsKey("X-Content-Type-Options"))
            headers["X-Content-Type-Options"] = "nosniff";

        if (!headers.ContainsKey("Referrer-Policy"))
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

        if (!headers.ContainsKey("X-Frame-Options"))
            headers["X-Frame-Options"] = "DENY";
    }
}
