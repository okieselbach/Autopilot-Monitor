using System.Diagnostics;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using AutopilotMonitor.Functions.Helpers;

namespace AutopilotMonitor.Functions.Middleware;

/// <summary>
/// Emits worker-side <see cref="RequestTelemetry"/> with business context (TenantId, UserId,
/// CorrelationId, UserRole) so the Application Insights <c>requests</c> table is queryable
/// by tenant, user, and correlation ID. Runs first in the pipeline to capture accurate
/// duration including auth and policy evaluation. Non-HTTP triggers are skipped.
/// </summary>
public class RequestTelemetryMiddleware : IFunctionsWorkerMiddleware
{
    private readonly TelemetryClient _telemetryClient;

    public RequestTelemetryMiddleware(TelemetryClient telemetryClient)
    {
        _telemetryClient = telemetryClient;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var httpContext = context.GetHttpContext();
        if (httpContext == null)
        {
            // Non-HTTP trigger (timer, queue) — nothing to track
            await next(context);
            return;
        }

        var sw = Stopwatch.StartNew();
        var startTime = DateTimeOffset.UtcNow;
        Exception? caughtException = null;

        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            caughtException = ex;
            throw;
        }
        finally
        {
            sw.Stop();

            var statusCode = caughtException != null ? 500 : httpContext.Response.StatusCode;
            var functionName = context.FunctionDefinition.Name;
            var method = httpContext.Request.Method;
            var url = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}{httpContext.Request.Path}{httpContext.Request.QueryString}";

            var requestTelemetry = new RequestTelemetry
            {
                Name = $"{method} {functionName}",
                Timestamp = startTime,
                Duration = sw.Elapsed,
                ResponseCode = statusCode.ToString(),
                Success = statusCode < 500,
                Url = new Uri(url),
            };

            // Distributed trace correlation
            var activity = Activity.Current;
            if (activity != null)
            {
                requestTelemetry.Context.Operation.Id = activity.RootId;
                requestTelemetry.Context.Operation.ParentId = activity.Id;
            }

            // Business context from downstream middleware
            requestTelemetry.Properties["Source"] = "WorkerMiddleware";
            requestTelemetry.Properties["FunctionName"] = functionName;
            requestTelemetry.Properties["HttpMethod"] = method;
            requestTelemetry.Properties["HttpPath"] = httpContext.Request.Path.Value ?? "";

            var clientSource = httpContext.Request.Headers["X-Client-Source"].FirstOrDefault();
            if (!string.IsNullOrEmpty(clientSource))
                requestTelemetry.Properties["ClientSource"] = clientSource;

            var mcpToolName = httpContext.Request.Headers["X-MCP-Tool-Name"].FirstOrDefault();
            if (!string.IsNullOrEmpty(mcpToolName))
                requestTelemetry.Properties["McpToolName"] = mcpToolName;

            if (context.Items.TryGetValue("CorrelationId", out var corrId) && corrId is string correlationId)
                requestTelemetry.Properties["CorrelationId"] = correlationId;

            var reqCtx = context.GetRequestContext();
            if (!string.IsNullOrEmpty(reqCtx.TenantId))
                requestTelemetry.Properties["TenantId"] = reqCtx.TenantId;
            if (!string.IsNullOrEmpty(reqCtx.UserPrincipalName))
                requestTelemetry.Properties["UserId"] = reqCtx.UserPrincipalName;
            if (!string.IsNullOrEmpty(reqCtx.UserRole))
                requestTelemetry.Properties["UserRole"] = reqCtx.UserRole;

            if (caughtException != null)
                requestTelemetry.Properties["ExceptionType"] = caughtException.GetType().Name;

            try
            {
                _telemetryClient.TrackRequest(requestTelemetry);
            }
            catch
            {
                // Never let telemetry failures mask the original exception or crash the pipeline
            }
        }
    }
}
