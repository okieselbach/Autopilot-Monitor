using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// Resolved request context populated by PolicyEnforcementMiddleware after authentication and policy evaluation.
/// Eliminates redundant IsGlobalAdminAsync / IsTenantAdminAsync service calls in function handlers.
/// Retrieved via <c>req.GetRequestContext()</c> or <c>context.GetRequestContext()</c>.
/// </summary>
public sealed record RequestContext
{
    internal const string ItemsKey = "RequestContext";

    /// <summary>The user's Azure AD tenant ID (from JWT tid claim).</summary>
    public string TenantId { get; init; } = string.Empty;

    /// <summary>The user's UPN (from JWT upn/preferred_username claim).</summary>
    public string UserPrincipalName { get; init; } = string.Empty;

    /// <summary>True if the user is a Global Admin of the platform.</summary>
    public bool IsGlobalAdmin { get; init; }

    /// <summary>True if the user is a Tenant Admin of their own tenant.</summary>
    public bool IsTenantAdmin { get; init; }

    /// <summary>The resolved role string (e.g. "GlobalAdmin", "Admin", "Operator", "Viewer").</summary>
    public string UserRole { get; init; } = string.Empty;
}

/// <summary>
/// Extension methods for accessing the resolved RequestContext.
/// </summary>
public static class RequestContextExtensions
{
    /// <summary>
    /// Gets the resolved RequestContext from FunctionContext.Items.
    /// Populated by PolicyEnforcementMiddleware for authenticated requests.
    /// Returns an empty context (all defaults) for device/anonymous routes.
    /// </summary>
    public static RequestContext GetRequestContext(this FunctionContext context)
    {
        if (context.Items.TryGetValue(RequestContext.ItemsKey, out var ctx) && ctx is RequestContext requestCtx)
            return requestCtx;
        return new RequestContext();
    }

    /// <summary>
    /// Gets the resolved RequestContext via the HTTP request's FunctionContext.
    /// </summary>
    public static RequestContext GetRequestContext(this HttpRequestData req)
        => req.FunctionContext.GetRequestContext();

    /// <summary>Gets the correlation ID for this request (set by CorrelationIdMiddleware).</summary>
    public static string GetCorrelationId(this FunctionContext context)
    {
        if (context.Items.TryGetValue("CorrelationId", out var id) && id is string correlationId)
            return correlationId;
        return string.Empty;
    }
}
