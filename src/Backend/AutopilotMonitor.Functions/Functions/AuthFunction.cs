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
    private readonly TenantConfigurationService _tenantConfigService;
    private readonly TenantAdminsService _tenantAdminsService;

    public AuthFunction(
        ILogger<AuthFunction> logger,
        GalacticAdminService galacticAdminService,
        TenantConfigurationService tenantConfigService,
        TenantAdminsService tenantAdminsService)
    {
        _logger = logger;
        _galacticAdminService = galacticAdminService;
        _tenantConfigService = tenantConfigService;
        _tenantAdminsService = tenantAdminsService;
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

        if (principal == null)
        {
            _logger.LogWarning("GetCurrentUser - No authentication found");
            return req.CreateResponse(HttpStatusCode.Unauthorized);
        }

        var tenantId = principal.GetTenantId();
        var upn = principal.GetUserPrincipalName();
        var displayName = principal.GetDisplayName();
        var objectId = principal.GetObjectId();

        // Validate required claims
        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(upn))
        {
            _logger.LogWarning("Missing required claims: tenantId or upn");
            var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequestResponse.WriteAsJsonAsync(new { error = "Missing required claims" });
            return badRequestResponse;
        }

        // Load tenant configuration
        var tenantConfig = await _tenantConfigService.GetConfigurationAsync(tenantId);

        // Extract and save domain name from first user if not set
        if (string.IsNullOrEmpty(tenantConfig.DomainName) && !string.IsNullOrEmpty(upn))
        {
            var domain = ExtractDomainFromUpn(upn);
            if (!string.IsNullOrEmpty(domain))
            {
                _logger.LogInformation($"Setting domain name for tenant {tenantId}: {domain}");
                tenantConfig.DomainName = domain;
                tenantConfig.UpdatedBy = upn;
                await _tenantConfigService.SaveConfigurationAsync(tenantConfig);
            }
        }

        // Check if tenant is disabled/suspended
        if (tenantConfig.IsCurrentlyDisabled())
        {
            _logger.LogWarning($"Login attempt for suspended tenant: {tenantId} by user {upn}");

            var suspendedResponse = req.CreateResponse(HttpStatusCode.Forbidden);
            await suspendedResponse.WriteAsJsonAsync(new
            {
                error = "TenantSuspended",
                message = !string.IsNullOrEmpty(tenantConfig.DisabledReason)
                    ? tenantConfig.DisabledReason
                    : "Your tenant has been suspended. Please contact support for more information.",
                disabledUntil = tenantConfig.DisabledUntil?.ToString("o"),
                contactSupport = true
            });
            return suspendedResponse;
        }

        // Check if user is galactic admin
        var isGalacticAdmin = await _galacticAdminService.IsGalacticAdminAsync(upn);

        // Check if user is tenant admin
        bool isTenantAdmin = await _tenantAdminsService.IsTenantAdminAsync(tenantId, upn);

        // Auto-admin logic: First user becomes admin
        if (!isTenantAdmin)
        {
            // Check if tenant has any admins at all
            var existingAdmins = await _tenantAdminsService.GetTenantAdminsAsync(tenantId);
            if (existingAdmins.Count == 0)
            {
                _logger.LogInformation($"First user login for tenant {tenantId}: {upn} - Auto-assigning as admin");
                await _tenantAdminsService.AddTenantAdminAsync(tenantId, upn, "System");
                isTenantAdmin = true;
            }
        }

        var userInfo = new
        {
            tenantId,
            upn,
            displayName,
            objectId,
            isGalacticAdmin,
            isTenantAdmin
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

    /// <summary>
    /// Extracts domain name from UPN (e.g., user@contoso.com -> contoso.com)
    /// </summary>
    private static string ExtractDomainFromUpn(string upn)
    {
        if (string.IsNullOrEmpty(upn))
            return string.Empty;

        var atIndex = upn.IndexOf('@');
        if (atIndex > 0 && atIndex < upn.Length - 1)
        {
            return upn.Substring(atIndex + 1);
        }

        return string.Empty;
    }
}

public class AddGalacticAdminRequest
{
    public string Upn { get; set; } = string.Empty;
}
