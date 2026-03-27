using System.Net;
using AutopilotMonitor.Functions.Extensions;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Admin;

/// <summary>
/// Tenant Offboarding endpoint
/// Allows a Tenant Admin to permanently delete all data for their tenant.
/// This operation is irreversible.
/// </summary>
public class TenantOffboardFunction
{
    private readonly ILogger<TenantOffboardFunction> _logger;
    private readonly TenantAdminsService _tenantAdminsService;
    private readonly GlobalAdminService _globalAdminService;
    private readonly IMaintenanceRepository _maintenanceRepo;

    public TenantOffboardFunction(
        ILogger<TenantOffboardFunction> logger,
        TenantAdminsService tenantAdminsService,
        GlobalAdminService globalAdminService,
        IMaintenanceRepository maintenanceRepo)
    {
        _logger = logger;
        _tenantAdminsService = tenantAdminsService;
        _globalAdminService = globalAdminService;
        _maintenanceRepo = maintenanceRepo;
    }

    /// <summary>
    /// DELETE /api/tenants/{tenantId}/offboard
    /// Permanently deletes ALL data for a tenant across all tables.
    /// Accessible by: Tenant Admins of the same tenant OR Global Admins.
    /// This action is IRREVERSIBLE.
    /// </summary>
    [Function("OffboardTenant")]
    [Authorize]
    public async Task<HttpResponseData> OffboardTenant(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "tenants/{tenantId}/offboard")] HttpRequestData req,
        string tenantId,
        FunctionContext context)
    {
        // Authentication enforced by PolicyEnforcementMiddleware
        var principal = context.GetUser()!;

        var userTenantId = principal.GetTenantId();
        var upn = principal.GetUserPrincipalName();

        // Only Tenant Admins of the same tenant OR Global Admins may offboard
        var isGlobalAdmin = await _globalAdminService.IsGlobalAdminAsync(upn);
        var isTenantAdmin = tenantId.Equals(userTenantId, StringComparison.OrdinalIgnoreCase) &&
                            await _tenantAdminsService.IsTenantAdminAsync(tenantId, upn);

        if (!isGlobalAdmin && !isTenantAdmin)
        {
            _logger.LogWarning($"User {upn} attempted to offboard tenant {tenantId} without authorization");
            var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
            await forbiddenResponse.WriteAsJsonAsync(new
            {
                error = "Access denied. Only a Tenant Admin of this tenant may perform offboarding."
            });
            return forbiddenResponse;
        }

        // Validate tenantId format to prevent OData injection
        SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

        _logger.LogWarning($"TENANT OFFBOARD initiated for tenant {tenantId} by {upn}");

        // Audit under Global TenantId so the entry survives tenant data deletion
        await _maintenanceRepo.LogAuditEntryAsync(
            Constants.AuditGlobalTenantId,
            "DELETE",
            "Tenant",
            tenantId,
            upn!,
            new Dictionary<string, string>
            {
                { "Action", "Offboard" },
                { "Phase", "Initiated" }
            });

        var result = new OffboardResult { TenantId = tenantId, InitiatedBy = upn!, InitiatedAt = DateTime.UtcNow };

        try
        {
            var deletedCounts = await _maintenanceRepo.DeleteAllTenantDataAsync(tenantId);
            result.DeletedCounts = deletedCounts;

            result.Success = true;
            _logger.LogWarning($"TENANT OFFBOARD completed for tenant {tenantId} by {upn}. " +
                $"Total rows deleted: {result.DeletedCounts.Values.Sum()}");

            // Audit completion with deletion summary
            var details = result.DeletedCounts.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
            details["Action"] = "Offboard";
            details["Phase"] = "Completed";
            details["TotalRowsDeleted"] = result.DeletedCounts.Values.Sum().ToString();
            await _maintenanceRepo.LogAuditEntryAsync(
                Constants.AuditGlobalTenantId,
                "DELETE",
                "Tenant",
                tenantId,
                upn!,
                details);

            var okResponse = req.CreateResponse(HttpStatusCode.OK);
            await okResponse.WriteAsJsonAsync(result);
            return okResponse;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Tenant offboard failed for tenant {tenantId}");
            result.Success = false;
            result.Error = "Internal server error";

            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(result);
            return errorResponse;
        }
    }

}

public class OffboardResult
{
    public string TenantId { get; set; } = string.Empty;
    public string InitiatedBy { get; set; } = string.Empty;
    public DateTime InitiatedAt { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public Dictionary<string, int> DeletedCounts { get; set; } = new();
}
