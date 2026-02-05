using System.Net;
using AutopilotMonitor.Functions.Extensions;
using AutopilotMonitor.Functions.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions;

/// <summary>
/// Authentication and authorization endpoints
/// </summary>
public class AuthFunction
{
    private readonly ILogger<AuthFunction> _logger;
    private readonly GalacticAdminService _galacticAdminService;

    public AuthFunction(
        ILogger<AuthFunction> logger,
        GalacticAdminService galacticAdminService)
    {
        _logger = logger;
        _galacticAdminService = galacticAdminService;
    }

    /// <summary>
    /// GET /api/auth/me
    /// Returns information about the currently authenticated user
    /// </summary>
    [Function("GetCurrentUser")]
    [Authorize]
    public async Task<HttpResponseData> GetCurrentUser(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auth/me")] HttpRequestData req,
        FunctionContext context)
    {
        var principal = context.GetUser();

        _logger.LogInformation($"GetCurrentUser - Principal is null: {principal == null}");

        if (principal == null)
        {
            _logger.LogWarning("GetCurrentUser - No ClaimsPrincipal found, returning Unauthorized");
            return req.CreateResponse(HttpStatusCode.Unauthorized);
        }

        _logger.LogInformation($"GetCurrentUser - Principal identity authenticated: {principal.Identity?.IsAuthenticated}, Claims count: {principal.Claims.Count()}");
        _logger.LogInformation($"GetCurrentUser - All claims: {string.Join(", ", principal.Claims.Select(c => $"{c.Type}={c.Value}"))}");

        var tenantId = principal.GetTenantId();
        var upn = principal.GetUserPrincipalName();
        var displayName = principal.GetDisplayName();
        var objectId = principal.GetObjectId();

        _logger.LogInformation($"GetCurrentUser - UPN: {upn}, ObjectId: {objectId}, TenantId: {tenantId}");

        // Check if user is galactic admin
        var isGalacticAdmin = await _galacticAdminService.IsGalacticAdminAsync(upn);

        var userInfo = new
        {
            tenantId,
            upn,
            displayName,
            objectId,
            isGalacticAdmin,
            claims = principal.GetAllClaims() // For debugging
        };

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(userInfo);
        return response;
    }

    /// <summary>
    /// GET /api/auth/is-galactic-admin
    /// Checks if the current user is a Galactic Admin
    /// </summary>
    [Function("IsGalacticAdmin")]
    [Authorize]
    public async Task<HttpResponseData> IsGalacticAdmin(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auth/is-galactic-admin")] HttpRequestData req,
        FunctionContext context)
    {
        var principal = context.GetUser();
        if (principal == null)
        {
            return req.CreateResponse(HttpStatusCode.Unauthorized);
        }

        var upn = principal.GetUserPrincipalName();
        var isAdmin = await _galacticAdminService.IsGalacticAdminAsync(upn);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { isGalacticAdmin = isAdmin, upn });
        return response;
    }

    /// <summary>
    /// GET /api/auth/galactic-admins
    /// Lists all Galactic Admins (only accessible by Galactic Admins)
    /// </summary>
    [Function("GetGalacticAdmins")]
    [Authorize]
    public async Task<HttpResponseData> GetGalacticAdmins(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auth/galactic-admins")] HttpRequestData req,
        FunctionContext context)
    {
        var principal = context.GetUser();
        if (principal == null)
        {
            return req.CreateResponse(HttpStatusCode.Unauthorized);
        }

        var upn = principal.GetUserPrincipalName();
        var isAdmin = await _galacticAdminService.IsGalacticAdminAsync(upn);

        if (!isAdmin)
        {
            var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
            await forbiddenResponse.WriteAsJsonAsync(new { error = "Only Galactic Admins can access this endpoint" });
            return forbiddenResponse;
        }

        var admins = await _galacticAdminService.GetAllGalacticAdminsAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { admins });
        return response;
    }

    /// <summary>
    /// POST /api/auth/galactic-admins
    /// Adds a new Galactic Admin (only accessible by existing Galactic Admins)
    /// </summary>
    [Function("AddGalacticAdmin")]
    [Authorize]
    public async Task<HttpResponseData> AddGalacticAdmin(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/galactic-admins")] HttpRequestData req,
        FunctionContext context)
    {
        var principal = context.GetUser();
        if (principal == null)
        {
            return req.CreateResponse(HttpStatusCode.Unauthorized);
        }

        var currentUpn = principal.GetUserPrincipalName();
        var isAdmin = await _galacticAdminService.IsGalacticAdminAsync(currentUpn);

        if (!isAdmin)
        {
            var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
            await forbiddenResponse.WriteAsJsonAsync(new { error = "Only Galactic Admins can add other admins" });
            return forbiddenResponse;
        }

        // Parse request body
        var body = await req.ReadFromJsonAsync<AddGalacticAdminRequest>();
        if (body == null || string.IsNullOrWhiteSpace(body.Upn))
        {
            var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequestResponse.WriteAsJsonAsync(new { error = "UPN is required" });
            return badRequestResponse;
        }

        var newAdmin = await _galacticAdminService.AddGalacticAdminAsync(body.Upn, currentUpn!);

        _logger.LogInformation($"Galactic Admin added: {body.Upn} by {currentUpn}");

        var response = req.CreateResponse(HttpStatusCode.Created);
        await response.WriteAsJsonAsync(new { admin = newAdmin });
        return response;
    }

    /// <summary>
    /// DELETE /api/auth/galactic-admins/{upn}
    /// Removes a Galactic Admin (only accessible by existing Galactic Admins)
    /// </summary>
    [Function("RemoveGalacticAdmin")]
    [Authorize]
    public async Task<HttpResponseData> RemoveGalacticAdmin(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "auth/galactic-admins/{upn}")] HttpRequestData req,
        string upn,
        FunctionContext context)
    {
        var principal = context.GetUser();
        if (principal == null)
        {
            return req.CreateResponse(HttpStatusCode.Unauthorized);
        }

        var currentUpn = principal.GetUserPrincipalName();
        var isAdmin = await _galacticAdminService.IsGalacticAdminAsync(currentUpn);

        if (!isAdmin)
        {
            var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
            await forbiddenResponse.WriteAsJsonAsync(new { error = "Only Galactic Admins can remove other admins" });
            return forbiddenResponse;
        }

        // Prevent self-removal
        if (upn.Equals(currentUpn, StringComparison.OrdinalIgnoreCase))
        {
            var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequestResponse.WriteAsJsonAsync(new { error = "You cannot remove yourself as a Galactic Admin" });
            return badRequestResponse;
        }

        await _galacticAdminService.RemoveGalacticAdminAsync(upn);

        _logger.LogInformation($"Galactic Admin removed: {upn} by {currentUpn}");

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "Galactic Admin removed successfully" });
        return response;
    }
}

public class AddGalacticAdminRequest
{
    public string Upn { get; set; } = string.Empty;
}
