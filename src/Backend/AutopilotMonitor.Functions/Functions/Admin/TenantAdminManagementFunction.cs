using System.Net;
using AutopilotMonitor.Functions.Extensions;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Admin;

/// <summary>
/// Tenant Admin Management endpoints
/// Allows tenant admins and galactic admins to manage admin users for a tenant
/// </summary>
public class TenantAdminManagementFunction
{
    private readonly ILogger<TenantAdminManagementFunction> _logger;
    private readonly TenantAdminsService _tenantAdminsService;
    private readonly GalacticAdminService _galacticAdminService;
    private readonly TableStorageService _storageService;

    public TenantAdminManagementFunction(
        ILogger<TenantAdminManagementFunction> logger,
        TenantAdminsService tenantAdminsService,
        GalacticAdminService galacticAdminService,
        TableStorageService storageService)
    {
        _logger = logger;
        _tenantAdminsService = tenantAdminsService;
        _galacticAdminService = galacticAdminService;
        _storageService = storageService;
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
        // Authentication enforced by PolicyEnforcementMiddleware
        var principal = context.GetUser()!;

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
        // Authentication enforced by PolicyEnforcementMiddleware
        var principal = context.GetUser()!;

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

        // Determine role (default to Admin for backward compat)
        var role = !string.IsNullOrWhiteSpace(body.Role) ? body.Role : AutopilotMonitor.Shared.Constants.TenantRoles.Admin;
        var newAdmin = await _tenantAdminsService.AddTenantMemberAsync(tenantId, body.Upn, upn!, role, body.CanManageBootstrapTokens);

        await _storageService.LogAuditEntryAsync(
            tenantId,
            "CREATE",
            "TenantAdmin",
            body.Upn,
            upn!,
            new Dictionary<string, string>
            {
                { "Role", role },
                { "CanManageBootstrapTokens", body.CanManageBootstrapTokens.ToString() }
            }
        );

        _logger.LogInformation($"Tenant member added: {body.Upn} with role {role} to tenant {tenantId} by {upn}");

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
        // Authentication enforced by PolicyEnforcementMiddleware
        var principal = context.GetUser()!;

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
            // Check if this is the last Admin-role member (only for non-Galactic-Admins)
            if (!isGalacticAdmin)
            {
                var members = await _tenantAdminsService.GetTenantAdminsAsync(tenantId);
                var adminCount = members.Count(m => m.IsEnabled && (m.Role == null || m.Role == AutopilotMonitor.Shared.Constants.TenantRoles.Admin));
                if (adminCount <= 1)
                {
                    var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequestResponse.WriteAsJsonAsync(new { error = "Cannot remove yourself as the last admin. Please add another admin first." });
                    return badRequestResponse;
                }
            }
        }

        // Remove the admin
        await _tenantAdminsService.RemoveTenantAdminAsync(tenantId, adminUpn);

        await _storageService.LogAuditEntryAsync(
            tenantId,
            "DELETE",
            "TenantAdmin",
            adminUpn,
            upn!
        );

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
        // Authentication enforced by PolicyEnforcementMiddleware
        var principal = context.GetUser()!;

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

        await _storageService.LogAuditEntryAsync(
            tenantId,
            "UPDATE",
            "TenantAdmin",
            adminUpn,
            upn!,
            new Dictionary<string, string> { { "Action", "Disable" } }
        );

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
        // Authentication enforced by PolicyEnforcementMiddleware
        var principal = context.GetUser()!;

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

        await _storageService.LogAuditEntryAsync(
            tenantId,
            "UPDATE",
            "TenantAdmin",
            adminUpn,
            upn!,
            new Dictionary<string, string> { { "Action", "Enable" } }
        );

        _logger.LogInformation($"Tenant Admin enabled: {adminUpn} for tenant {tenantId} by {upn}");

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "Tenant Admin enabled successfully" });
        return response;
    }
    /// <summary>
    /// PATCH /api/tenants/{tenantId}/admins/{adminUpn}/permissions
    /// Updates role and permissions for a tenant member
    /// Accessible by: Galactic Admins OR Tenant Admins of the same tenant
    /// </summary>
    [Function("UpdateMemberPermissions")]
    [Authorize]
    public async Task<HttpResponseData> UpdateMemberPermissions(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "tenants/{tenantId}/admins/{adminUpn}/permissions")] HttpRequestData req,
        string tenantId,
        string adminUpn,
        FunctionContext context)
    {
        // Authentication enforced by PolicyEnforcementMiddleware
        var principal = context.GetUser()!;

        var userTenantId = principal.GetTenantId();
        var upn = principal.GetUserPrincipalName();

        // Check authorization: either Galactic Admin OR Tenant Admin of the same tenant
        var isGalacticAdmin = await _galacticAdminService.IsGalacticAdminAsync(upn);
        var isTenantAdmin = tenantId.Equals(userTenantId, StringComparison.OrdinalIgnoreCase) &&
                           await _tenantAdminsService.IsTenantAdminAsync(tenantId, upn);

        if (!isGalacticAdmin && !isTenantAdmin)
        {
            _logger.LogWarning("User {Upn} attempted to update member permissions for tenant {TenantId} without authorization", upn, tenantId);
            var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
            await forbiddenResponse.WriteAsJsonAsync(new { error = "Access denied. You must be a Galactic Admin or a Tenant Admin for this tenant." });
            return forbiddenResponse;
        }

        var body = await req.ReadFromJsonAsync<UpdateMemberPermissionsRequest>();
        if (body == null || string.IsNullOrWhiteSpace(body.Role))
        {
            var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequestResponse.WriteAsJsonAsync(new { error = "Role is required" });
            return badRequestResponse;
        }

        // Prevent demoting yourself if you're the last Admin
        if (adminUpn.Equals(upn, StringComparison.OrdinalIgnoreCase) && body.Role != AutopilotMonitor.Shared.Constants.TenantRoles.Admin)
        {
            if (!isGalacticAdmin)
            {
                var members = await _tenantAdminsService.GetTenantAdminsAsync(tenantId);
                var adminCount = members.Count(m => m.IsEnabled && (m.Role == null || m.Role == AutopilotMonitor.Shared.Constants.TenantRoles.Admin));
                if (adminCount <= 1)
                {
                    var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequestResponse.WriteAsJsonAsync(new { error = "Cannot demote yourself as the last admin. Please add another admin first." });
                    return badRequestResponse;
                }
            }
        }

        var updated = await _tenantAdminsService.UpdateMemberPermissionsAsync(tenantId, adminUpn, body.Role, body.CanManageBootstrapTokens);
        if (!updated)
        {
            var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
            await notFoundResponse.WriteAsJsonAsync(new { error = "Member not found" });
            return notFoundResponse;
        }

        await _storageService.LogAuditEntryAsync(
            tenantId,
            "UPDATE",
            "TenantAdmin",
            adminUpn,
            upn!,
            new Dictionary<string, string>
            {
                { "Action", "UpdatePermissions" },
                { "Role", body.Role },
                { "CanManageBootstrapTokens", body.CanManageBootstrapTokens.ToString() }
            }
        );

        _logger.LogInformation("Member permissions updated: {AdminUpn} -> role={Role} in tenant {TenantId} by {Upn}", adminUpn, body.Role, tenantId, upn);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "Member permissions updated successfully" });
        return response;
    }
}

public class AddTenantAdminRequest
{
    public string Upn { get; set; } = string.Empty;
    public string? Role { get; set; }
    public bool CanManageBootstrapTokens { get; set; }
}

public class UpdateMemberPermissionsRequest
{
    public string Role { get; set; } = string.Empty;
    public bool CanManageBootstrapTokens { get; set; }
}
