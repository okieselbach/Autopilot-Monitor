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
    /// Extracts the tenant ID from the authenticated user's JWT token claims.
    /// Uses the Azure AD tenant ID ('tid' claim) which identifies which customer/organization owns the data.
    ///
    /// Normal Users: Can only see sessions with their own tenant ID (from JWT 'tid' claim)
    /// </summary>
    /// <param name="req">The HTTP request</param>
    /// <returns>The tenant ID from the JWT token</returns>
    /// <exception cref="UnauthorizedAccessException">Thrown when user is not authenticated or tenant ID is missing</exception>
    public static string GetTenantId(HttpRequestData req)
    {
        var httpContext = req.FunctionContext.GetHttpContext();
        var isAuthenticated = httpContext?.User?.Identity?.IsAuthenticated == true;

        if (!isAuthenticated)
        {
            throw new UnauthorizedAccessException("User is not authenticated. JWT token required.");
        }

        var user = httpContext!.User;

        // Extract tenant ID from JWT 'tid' claim (Azure AD tenant ID)
        var tenantIdClaim = user.FindFirst("tid")?.Value;
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
        var httpContext = req.FunctionContext.GetHttpContext();
        if (httpContext?.User?.Identity?.IsAuthenticated != true)
        {
            return "Anonymous";
        }

        var user = httpContext.User;

        // Try to get email first, then preferred_username, then name
        return user.FindFirst(ClaimTypes.Email)?.Value ??
               user.FindFirst("preferred_username")?.Value ??
               user.FindFirst(ClaimTypes.Name)?.Value ??
               user.FindFirst("name")?.Value ??
               "Unknown";
    }
}
