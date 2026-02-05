using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using System.Security.Claims;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// Helper class for extracting tenant information from authenticated requests
/// </summary>
public static class TenantHelper
{
    /// <summary>
    /// Checks if the request has an authenticated user
    /// Azure Functions Isolated Worker compatible
    /// </summary>
    /// <param name="req">The HTTP request</param>
    /// <returns>True if user is authenticated, false otherwise</returns>
    public static bool IsAuthenticated(HttpRequestData req)
    {
        // Azure Functions Isolated Worker: Try FunctionContext.Items first
        // This is set by AuthenticationMiddleware and is more reliable than httpContext.User
        if (req.FunctionContext.Items.TryGetValue("ClaimsPrincipal", out var principalObj)
            && principalObj is ClaimsPrincipal principal)
        {
            return principal.Identity?.IsAuthenticated == true;
        }

        // Fallback to HTTP context (may not work reliably in isolated worker)
        var httpContext = req.FunctionContext.GetHttpContext();
        return httpContext?.User?.Identity?.IsAuthenticated == true;
    }

    /// <summary>
    /// Extracts the tenant ID from the authenticated user's JWT token claims.
    /// Uses the Azure AD tenant ID claim which identifies which customer/organization owns the data.
    /// Supports both v1.0 and v2.0 tokens.
    ///
    /// Normal Users: Can only see sessions with their own tenant ID (from JWT token)
    /// </summary>
    /// <param name="req">The HTTP request</param>
    /// <returns>The tenant ID from the JWT token</returns>
    /// <exception cref="UnauthorizedAccessException">Thrown when user is not authenticated or tenant ID is missing</exception>
    public static string GetTenantId(HttpRequestData req)
    {
        // Azure Functions Isolated Worker: Try FunctionContext.Items first
        // This is set by AuthenticationMiddleware and is more reliable than httpContext.User
        ClaimsPrincipal? user = null;

        if (req.FunctionContext.Items.TryGetValue("ClaimsPrincipal", out var principalObj)
            && principalObj is ClaimsPrincipal principal)
        {
            user = principal;
        }
        else
        {
            // Fallback to HTTP context (may not work reliably in isolated worker)
            var httpContext = req.FunctionContext.GetHttpContext();
            if (httpContext?.User?.Identity?.IsAuthenticated == true)
            {
                user = httpContext.User;
            }
        }

        if (user?.Identity?.IsAuthenticated != true)
        {
            throw new UnauthorizedAccessException("User is not authenticated. JWT token required.");
        }

        // Extract tenant ID from JWT token
        // v2.0 tokens: "tid" claim
        // v1.0 tokens: "http://schemas.microsoft.com/identity/claims/tenantid" claim
        var tenantIdClaim = user.FindFirst("tid")?.Value ??
                           user.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid")?.Value;

        if (string.IsNullOrEmpty(tenantIdClaim))
        {
            throw new UnauthorizedAccessException("Tenant ID (tid) claim not found in token");
        }

        // Return the tenant ID from the JWT token
        return tenantIdClaim;
    }

    /// <summary>
    /// Gets the authenticated user's email or name for audit logging
    /// </summary>
    /// <param name="req">The HTTP request</param>
    /// <returns>User email or name, or "Anonymous" if not authenticated</returns>
    public static string GetUserIdentifier(HttpRequestData req)
    {
        // Azure Functions Isolated Worker: Try FunctionContext.Items first
        ClaimsPrincipal? user = null;

        if (req.FunctionContext.Items.TryGetValue("ClaimsPrincipal", out var principalObj)
            && principalObj is ClaimsPrincipal principal)
        {
            user = principal;
        }
        else
        {
            // Fallback to HTTP context
            var httpContext = req.FunctionContext.GetHttpContext();
            if (httpContext?.User?.Identity?.IsAuthenticated == true)
            {
                user = httpContext.User;
            }
        }

        if (user?.Identity?.IsAuthenticated != true)
        {
            return "Anonymous";
        }

        // Try to get UPN first (Azure AD User Principal Name - most reliable identifier)
        // Then fall back to email, preferred_username, and finally name
        return user.FindFirst("upn")?.Value ??
               user.FindFirst(ClaimTypes.Upn)?.Value ??
               user.FindFirst(ClaimTypes.Email)?.Value ??
               user.FindFirst("preferred_username")?.Value ??
               user.FindFirst(ClaimTypes.Name)?.Value ??
               user.FindFirst("name")?.Value ??
               "Unknown";
    }
}
