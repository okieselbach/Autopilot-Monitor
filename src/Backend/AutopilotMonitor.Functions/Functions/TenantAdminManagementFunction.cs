using System.Net;
using AutopilotMonitor.Functions.Extensions;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions;

/// <summary>
/// Tenant Admin Management endpoints
/// Allows tenant admins and galactic admins to manage admin users for a tenant
/// </summary>
public class TenantAdminManagementFunction
{
    private readonly ILogger<TenantAdminManagementFunction> _logger;
    private readonly TenantAdminsService _tenantAdminsService;
    private readonly GalacticAdminService _galacticAdminService;

    public TenantAdminManagementFunction(
        ILogger<TenantAdminManagementFunction> logger,
        TenantAdminsService tenantAdminsService,
        GalacticAdminService galacticAdminService)
    {
        _logger = logger;
        _tenantAdminsService = tenantAdminsService;
        _galacticAdminService = galacticAdminService;
    }

    /// <summary>
    /// GET /api/tenants/{tenantId}/admins
    /// Gets all admins for a tenant
    /// Accessible by: Galactic Admins OR Tenant Admins of the same tenant
    /// </summary>
    [Function("GetTenantAdmins")]
    [Authorize]
    public async Task<HttpResponseData> GetTenantAdmins(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "tenants/{tenantId}/admins")] HttpRequestData req,
        string tenantId,
        FunctionContext context)
    {
        var principal = context.GetUser();
        if (principal == null)
        {
            return req.CreateResponse(HttpStatusCode.Unauthorized);
        }

        var userTenantId = principal.GetTenantId();
        var upn = principal.GetUserPrincipalName();

        // Check authorization: either Galactic Admin OR Tenant Admin of the same tenant
        var isGalacticAdmin = await _galacticAdminService.IsGalacticAdminAsync(upn);
        var isTenantAdmin = tenantId.Equals(userTenantId, StringComparison.OrdinalIgnoreCase) &&
                           await _tenantAdminsService.IsTenantAdminAsync(tenantId, upn);

        if (!isGalacticAdmin && !isTenantAdmin)
        {
            _logger.LogWarning($"User {upn} attempted to access admins for tenant {tenantId} without authorization");
            var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
            await forbiddenResponse.WriteAsJsonAsync(new { error = "Access denied. You must be a Galactic Admin or a Tenant Admin for this tenant." });
            return forbiddenResponse;
        }

        var admins = await _tenantAdminsService.GetTenantAdminsAsync(tenantId);

        _logger.LogInformation($"Retrieved {admins.Count} admins for tenant {tenantId} by {upn}");

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(admins);
        return response;
    }

    /// <summary>
    /// POST /api/tenants/{tenantId}/admins
    /// Adds a new admin to a tenant
    /// Accessible by: Galactic Admins OR Tenant Admins of the same tenant
    /// </summary>
    [Function("AddTenantAdmin")]
    [Authorize]
    public async Task<HttpResponseData> AddTenantAdmin(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "tenants/{tenantId}/admins")] HttpRequestData req,
        string tenantId,
        FunctionContext context)
    {
        var principal = context.GetUser();
        if (principal == null)
        {
            return req.CreateResponse(HttpStatusCode.Unauthorized);
        }

        var userTenantId = principal.GetTenantId();
        var upn = principal.GetUserPrincipalName();

        // Check authorization: either Galactic Admin OR Tenant Admin of the same tenant
        var isGalacticAdmin = await _galacticAdminService.IsGalacticAdminAsync(upn);
        var isTenantAdmin = tenantId.Equals(userTenantId, StringComparison.OrdinalIgnoreCase) &&
                           await _tenantAdminsService.IsTenantAdminAsync(tenantId, upn);

        if (!isGalacticAdmin && !isTenantAdmin)
        {
            _logger.LogWarning($"User {upn} attempted to add admin to tenant {tenantId} without authorization");
            var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
            await forbiddenResponse.WriteAsJsonAsync(new { error = "Access denied. You must be a Galactic Admin or a Tenant Admin for this tenant." });
            return forbiddenResponse;
        }

        // Parse request body
        var body = await req.ReadFromJsonAsync<AddTenantAdminRequest>();
        if (body == null || string.IsNullOrWhiteSpace(body.Upn))
        {
            var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequestResponse.WriteAsJsonAsync(new { error = "UPN is required" });
            return badRequestResponse;
        }

        // Add the admin
        var newAdmin = await _tenantAdminsService.AddTenantAdminAsync(tenantId, body.Upn, upn!);

        _logger.LogInformation($"Tenant Admin added: {body.Upn} to tenant {tenantId} by {upn}");

        var response = req.CreateResponse(HttpStatusCode.Created);
        await response.WriteAsJsonAsync(new { admin = newAdmin });
        return response;
    }

    /// <summary>
    /// DELETE /api/tenants/{tenantId}/admins/{adminUpn}
    /// Removes an admin from a tenant
    /// Accessible by: Galactic Admins OR Tenant Admins of the same tenant
    /// Note: Cannot remove yourself if you're the last admin
    /// </summary>
    [Function("RemoveTenantAdmin")]
    [Authorize]
    public async Task<HttpResponseData> RemoveTenantAdmin(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "tenants/{tenantId}/admins/{adminUpn}")] HttpRequestData req,
        string tenantId,
        string adminUpn,
        FunctionContext context)
    {
        var principal = context.GetUser();
        if (principal == null)
        {
            return req.CreateResponse(HttpStatusCode.Unauthorized);
        }

        var userTenantId = principal.GetTenantId();
        var upn = principal.GetUserPrincipalName();

        // Check authorization: either Galactic Admin OR Tenant Admin of the same tenant
        var isGalacticAdmin = await _galacticAdminService.IsGalacticAdminAsync(upn);
        var isTenantAdmin = tenantId.Equals(userTenantId, StringComparison.OrdinalIgnoreCase) &&
                           await _tenantAdminsService.IsTenantAdminAsync(tenantId, upn);

        if (!isGalacticAdmin && !isTenantAdmin)
        {
            _logger.LogWarning($"User {upn} attempted to remove admin from tenant {tenantId} without authorization");
            var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
            await forbiddenResponse.WriteAsJsonAsync(new { error = "Access denied. You must be a Galactic Admin or a Tenant Admin for this tenant." });
            return forbiddenResponse;
        }

        // Check if trying to remove self
        if (adminUpn.Equals(upn, StringComparison.OrdinalIgnoreCase))
        {
            // Check if this is the last admin (only for non-Galactic-Admins)
            if (!isGalacticAdmin)
            {
                var admins = await _tenantAdminsService.GetTenantAdminsAsync(tenantId);
                if (admins.Count == 1)
                {
                    var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequestResponse.WriteAsJsonAsync(new { error = "Cannot remove yourself as the last admin. Please add another admin first." });
                    return badRequestResponse;
                }
            }
        }

        // Remove the admin
        await _tenantAdminsService.RemoveTenantAdminAsync(tenantId, adminUpn);

        _logger.LogInformation($"Tenant Admin removed: {adminUpn} from tenant {tenantId} by {upn}");

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "Tenant Admin removed successfully" });
        return response;
    }

    /// <summary>
    /// PATCH /api/tenants/{tenantId}/admins/{adminUpn}/disable
    /// Disables an admin for a tenant
    /// Accessible by: Galactic Admins OR Tenant Admins of the same tenant
    /// </summary>
    [Function("DisableTenantAdmin")]
    [Authorize]
    public async Task<HttpResponseData> DisableTenantAdmin(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "tenants/{tenantId}/admins/{adminUpn}/disable")] HttpRequestData req,
        string tenantId,
        string adminUpn,
        FunctionContext context)
    {
        var principal = context.GetUser();
        if (principal == null)
        {
            return req.CreateResponse(HttpStatusCode.Unauthorized);
        }

        var userTenantId = principal.GetTenantId();
        var upn = principal.GetUserPrincipalName();

        // Check authorization: either Galactic Admin OR Tenant Admin of the same tenant
        var isGalacticAdmin = await _galacticAdminService.IsGalacticAdminAsync(upn);
        var isTenantAdmin = tenantId.Equals(userTenantId, StringComparison.OrdinalIgnoreCase) &&
                           await _tenantAdminsService.IsTenantAdminAsync(tenantId, upn);

        if (!isGalacticAdmin && !isTenantAdmin)
        {
            _logger.LogWarning($"User {upn} attempted to disable admin for tenant {tenantId} without authorization");
            var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
            await forbiddenResponse.WriteAsJsonAsync(new { error = "Access denied. You must be a Galactic Admin or a Tenant Admin for this tenant." });
            return forbiddenResponse;
        }

        // Disable the admin
        await _tenantAdminsService.DisableTenantAdminAsync(tenantId, adminUpn);

        _logger.LogInformation($"Tenant Admin disabled: {adminUpn} for tenant {tenantId} by {upn}");

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "Tenant Admin disabled successfully" });
        return response;
    }

    /// <summary>
    /// PATCH /api/tenants/{tenantId}/admins/{adminUpn}/enable
    /// Enables an admin for a tenant
    /// Accessible by: Galactic Admins OR Tenant Admins of the same tenant
    /// </summary>
    [Function("EnableTenantAdmin")]
    [Authorize]
    public async Task<HttpResponseData> EnableTenantAdmin(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "tenants/{tenantId}/admins/{adminUpn}/enable")] HttpRequestData req,
        string tenantId,
        string adminUpn,
        FunctionContext context)
    {
        var principal = context.GetUser();
        if (principal == null)
        {
            return req.CreateResponse(HttpStatusCode.Unauthorized);
        }

        var userTenantId = principal.GetTenantId();
        var upn = principal.GetUserPrincipalName();

        // Check authorization: either Galactic Admin OR Tenant Admin of the same tenant
        var isGalacticAdmin = await _galacticAdminService.IsGalacticAdminAsync(upn);
        var isTenantAdmin = tenantId.Equals(userTenantId, StringComparison.OrdinalIgnoreCase) &&
                           await _tenantAdminsService.IsTenantAdminAsync(tenantId, upn);

        if (!isGalacticAdmin && !isTenantAdmin)
        {
            _logger.LogWarning($"User {upn} attempted to enable admin for tenant {tenantId} without authorization");
            var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
            await forbiddenResponse.WriteAsJsonAsync(new { error = "Access denied. You must be a Galactic Admin or a Tenant Admin for this tenant." });
            return forbiddenResponse;
        }

        // Enable the admin
        await _tenantAdminsService.EnableTenantAdminAsync(tenantId, adminUpn);

        _logger.LogInformation($"Tenant Admin enabled: {adminUpn} for tenant {tenantId} by {upn}");

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "Tenant Admin enabled successfully" });
        return response;
    }
}

public class AddTenantAdminRequest
{
    public string Upn { get; set; } = string.Empty;
}
