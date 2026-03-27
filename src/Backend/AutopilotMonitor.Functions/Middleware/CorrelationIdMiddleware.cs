using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Middleware;

/// <summary>
/// Middleware that ensures every request carries a Correlation ID.
/// Reads <c>X-Correlation-ID</c> from the incoming request; generates a new compact GUID if absent.
/// The ID is stored in <c>FunctionContext.Items["CorrelationId"]</c> (retrieve via
/// <c>context.GetCorrelationId()</c>), echoed back in the <c>X-Correlation-ID</c> response header,
/// and injected into the logging scope so all log entries for the request carry it automatically.
/// </summary>
public class CorrelationIdMiddleware : IFunctionsWorkerMiddleware
{
    private const string HeaderName = "X-Correlation-ID";
    private const string ItemsKey = "CorrelationId";
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(ILogger<CorrelationIdMiddleware> logger)
    {
        _logger = logger;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var httpContext = context.GetHttpContext();

        string correlationId;
        if (httpContext != null
            && httpContext.Request.Headers.TryGetValue(HeaderName, out var existingId)
            && !string.IsNullOrEmpty(existingId))
        {
            correlationId = existingId.ToString();
        }
        else
        {
            correlationId = Guid.NewGuid().ToString("N");
        }

        context.Items[ItemsKey] = correlationId;

        if (httpContext != null)
        {
            httpContext.Response.OnStarting(() =>
            {
                if (!httpContext.Response.Headers.ContainsKey(HeaderName))
                    httpContext.Response.Headers[HeaderName] = correlationId;
                return Task.CompletedTask;
            });
        }

        using (_logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }
}
