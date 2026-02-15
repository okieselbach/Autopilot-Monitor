using System.Net;
using AutopilotMonitor.Functions.Extensions;
using AutopilotMonitor.Functions.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions;

/// <summary>
/// CRUD endpoints for managing the Private Preview tenant whitelist.
/// All endpoints are Galactic Admin only.
/// Temporary — remove after GA.
/// </summary>
public class PreviewWhitelistFunction
{
    private readonly ILogger<PreviewWhitelistFunction> _logger;
    private readonly PreviewWhitelistService _previewWhitelistService;
    private readonly GalacticAdminService _galacticAdminService;

    public PreviewWhitelistFunction(
        ILogger<PreviewWhitelistFunction> logger,
        PreviewWhitelistService previewWhitelistService,
        GalacticAdminService galacticAdminService)
    {
        _logger = logger;
        _previewWhitelistService = previewWhitelistService;
        _galacticAdminService = galacticAdminService;
    }

    /// <summary>
    /// GET /api/preview-whitelist
    /// Returns all approved tenants.
    /// </summary>
    [Function("GetPreviewWhitelist")]
    [Authorize]
    public async Task<HttpResponseData> GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "preview-whitelist")] HttpRequestData req,
        FunctionContext context)
    {
        var principal = context.GetUser();
        if (principal == null)
            return req.CreateResponse(HttpStatusCode.Unauthorized);

        var upn = principal.GetUserPrincipalName();
        if (!await _galacticAdminService.IsGalacticAdminAsync(upn))
        {
            var forbidden = req.CreateResponse(HttpStatusCode.Forbidden);
            await forbidden.WriteAsJsonAsync(new { error = "Only Galactic Admins can access this endpoint" });
            return forbidden;
        }

        var approved = await _previewWhitelistService.GetAllApprovedAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { tenants = approved });
        return response;
    }

    /// <summary>
    /// POST /api/preview-whitelist/{tenantId}
    /// Approves a tenant for Private Preview.
    /// </summary>
    [Function("ApprovePreviewTenant")]
    [Authorize]
    public async Task<HttpResponseData> Approve(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "preview-whitelist/{tenantId}")] HttpRequestData req,
        string tenantId,
        FunctionContext context)
    {
        var principal = context.GetUser();
        if (principal == null)
            return req.CreateResponse(HttpStatusCode.Unauthorized);

        var upn = principal.GetUserPrincipalName();
        if (!await _galacticAdminService.IsGalacticAdminAsync(upn))
        {
            var forbidden = req.CreateResponse(HttpStatusCode.Forbidden);
            await forbidden.WriteAsJsonAsync(new { error = "Only Galactic Admins can approve tenants" });
            return forbidden;
        }

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteAsJsonAsync(new { error = "tenantId is required" });
            return bad;
        }

        await _previewWhitelistService.ApproveAsync(tenantId, upn!);

        _logger.LogInformation("Preview tenant approved: {TenantId} by {Upn}", tenantId, upn);

        var response = req.CreateResponse(HttpStatusCode.Created);
        await response.WriteAsJsonAsync(new { message = "Tenant approved for preview", tenantId });
        return response;
    }

    /// <summary>
    /// DELETE /api/preview-whitelist/{tenantId}
    /// Revokes a tenant's Private Preview access.
    /// </summary>
    [Function("RevokePreviewTenant")]
    [Authorize]
    public async Task<HttpResponseData> Revoke(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "preview-whitelist/{tenantId}")] HttpRequestData req,
        string tenantId,
        FunctionContext context)
    {
        var principal = context.GetUser();
        if (principal == null)
            return req.CreateResponse(HttpStatusCode.Unauthorized);

        var upn = principal.GetUserPrincipalName();
        if (!await _galacticAdminService.IsGalacticAdminAsync(upn))
        {
            var forbidden = req.CreateResponse(HttpStatusCode.Forbidden);
            await forbidden.WriteAsJsonAsync(new { error = "Only Galactic Admins can revoke tenant access" });
            return forbidden;
        }

        await _previewWhitelistService.RevokeAsync(tenantId);

        _logger.LogInformation("Preview tenant revoked: {TenantId} by {Upn}", tenantId, upn);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "Tenant removed from preview", tenantId });
        return response;
    }
}
