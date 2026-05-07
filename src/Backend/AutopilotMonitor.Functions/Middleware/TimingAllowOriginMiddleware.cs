using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;

namespace AutopilotMonitor.Functions.Middleware;

/// <summary>
/// Adds the <c>Timing-Allow-Origin: *</c> response header so cross-origin
/// callers (notably the portal) can read full Resource Timing details
/// (<c>transferSize</c>, <c>encodedBodySize</c>, <c>nextHopProtocol</c>, ...)
/// from the browser's <c>performance.getEntriesByType('resource')</c>.
/// Without this header those fields stay zero / "unknown" and our own
/// real-user monitoring cannot see API payload sizes.
/// </summary>
public class TimingAllowOriginMiddleware : IFunctionsWorkerMiddleware
{
    private const string HeaderName = "Timing-Allow-Origin";
    private const string HeaderValue = "*";

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var httpContext = context.GetHttpContext();
        if (httpContext != null)
        {
            httpContext.Response.OnStarting(() =>
            {
                if (!httpContext.Response.Headers.ContainsKey(HeaderName))
                    httpContext.Response.Headers[HeaderName] = HeaderValue;
                return Task.CompletedTask;
            });
        }

        await next(context);
    }
}
