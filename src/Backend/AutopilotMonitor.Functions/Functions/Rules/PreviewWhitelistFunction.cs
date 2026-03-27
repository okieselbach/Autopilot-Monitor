using System.Net;
using AutopilotMonitor.Functions.Extensions;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Rules;

/// <summary>
/// CRUD endpoints for managing the Private Preview tenant whitelist.
/// All endpoints are Global Admin only (except notification-email which is AuthenticatedUser).
/// Temporary — remove after GA.
/// </summary>
public class PreviewWhitelistFunction
{
    private readonly ILogger<PreviewWhitelistFunction> _logger;
    private readonly PreviewWhitelistService _previewWhitelistService;
    private readonly TenantConfigurationService _tenantConfigurationService;
    private readonly TenantAdminsService _tenantAdminsService;
    private readonly ResendEmailService _resendEmailService;

    public PreviewWhitelistFunction(
        ILogger<PreviewWhitelistFunction> logger,
        PreviewWhitelistService previewWhitelistService,
        TenantConfigurationService tenantConfigurationService,
        TenantAdminsService tenantAdminsService,
        ResendEmailService resendEmailService)
    {
        _logger = logger;
        _previewWhitelistService = previewWhitelistService;
        _tenantConfigurationService = tenantConfigurationService;
        _tenantAdminsService = tenantAdminsService;
        _resendEmailService = resendEmailService;
    }

    /// <summary>
    /// GET /api/preview/whitelist
    /// Returns all approved tenants.
    /// </summary>
    [Function("GetPreviewWhitelist")]
    [Authorize]
    public async Task<HttpResponseData> GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "preview/whitelist")] HttpRequestData req,
        FunctionContext context)
    {
        // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware

        var approved = await _previewWhitelistService.GetAllApprovedAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { tenants = approved });
        return response;
    }

    /// <summary>
    /// POST /api/preview/whitelist/{tenantId}
    /// Approves a tenant for Private Preview.
    /// </summary>
    [Function("ApprovePreviewTenant")]
    [Authorize]
    public async Task<HttpResponseData> Approve(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "preview/whitelist/{tenantId}")] HttpRequestData req,
        string tenantId,
        FunctionContext context)
    {
        // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
        var principal = context.GetUser();
        var upn = principal?.GetUserPrincipalName();

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteAsJsonAsync(new { error = "tenantId is required" });
            return bad;
        }

        await _previewWhitelistService.ApproveAsync(tenantId, upn!);

        _logger.LogInformation("Preview tenant approved: {TenantId} by {Upn}", tenantId, upn);

        // Auto-promote the tenant requester (first user who triggered tenant config creation)
        // as TenantAdmin if they are not already one.
        // This ensures whoever signed up doesn't need manual admin assignment after approval.
        try
        {
            var tenantConfig = await _tenantConfigurationService.GetConfigurationAsync(tenantId);
            var requesterUpn = tenantConfig.UpdatedBy;

            if (!string.IsNullOrWhiteSpace(requesterUpn)
                && !string.Equals(requesterUpn, "System", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(requesterUpn, "System (auto-re-enable)", StringComparison.OrdinalIgnoreCase))
            {
                var isAlreadyAdmin = await _tenantAdminsService.IsTenantAdminAsync(tenantId, requesterUpn);
                if (!isAlreadyAdmin)
                {
                    await _tenantAdminsService.AddTenantAdminAsync(tenantId, requesterUpn, upn!);
                    _logger.LogInformation(
                        "Auto-promoted tenant requester {RequesterUpn} as TenantAdmin for tenant {TenantId} on preview approval by {ApprovedBy}",
                        requesterUpn, tenantId, upn);
                }
                else
                {
                    _logger.LogInformation(
                        "Tenant requester {RequesterUpn} is already a TenantAdmin for tenant {TenantId} — skipping auto-promote",
                        requesterUpn, tenantId);
                }
            }
            else
            {
                _logger.LogInformation(
                    "No valid tenant requester UPN found in TenantConfiguration for tenant {TenantId} (UpdatedBy: '{UpdatedBy}') — skipping auto-promote",
                    tenantId, requesterUpn ?? "<null>");
            }

            // Fire-and-forget: send welcome email if notification email is configured
            var notificationEmail = await _previewWhitelistService.GetNotificationEmailAsync(tenantId);
            if (!string.IsNullOrWhiteSpace(notificationEmail))
            {
                _ = _resendEmailService.SendPreviewApprovedEmailAsync(
                        notificationEmail, tenantConfig.DomainName)
                    .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                        "Fire-and-forget welcome email failed for tenant {TenantId}", tenantId),
                        TaskContinuationOptions.OnlyOnFaulted);
            }
        }
        catch (Exception ex)
        {
            // Non-fatal: approval already succeeded, admin promotion is best-effort
            _logger.LogWarning(ex,
                "Failed to auto-promote tenant requester as TenantAdmin for tenant {TenantId} — approval still succeeded",
                tenantId);
        }

        var response = req.CreateResponse(HttpStatusCode.Created);
        await response.WriteAsJsonAsync(new { message = "Tenant approved for preview", tenantId });
        return response;
    }

    /// <summary>
    /// DELETE /api/preview/whitelist/{tenantId}
    /// Revokes a tenant's Private Preview access.
    /// </summary>
    [Function("RevokePreviewTenant")]
    [Authorize]
    public async Task<HttpResponseData> Revoke(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "preview/whitelist/{tenantId}")] HttpRequestData req,
        string tenantId,
        FunctionContext context)
    {
        // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
        var principal = context.GetUser();
        var upn = principal?.GetUserPrincipalName();

        await _previewWhitelistService.RevokeAsync(tenantId);

        _logger.LogInformation("Preview tenant revoked: {TenantId} by {Upn}", tenantId, upn);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "Tenant removed from preview", tenantId });
        return response;
    }

    /// <summary>
    /// GET /api/preview/notification-email/{tenantId}
    /// Returns the notification email for a tenant. Global Admin only.
    /// </summary>
    [Function("GetPreviewNotificationEmail")]
    [Authorize]
    public async Task<HttpResponseData> GetNotificationEmail(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "preview/notification-email/{tenantId}")] HttpRequestData req,
        string tenantId,
        FunctionContext context)
    {
        var email = await _previewWhitelistService.GetNotificationEmailAsync(tenantId);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { email = email ?? "" });
        return response;
    }

    /// <summary>
    /// PUT /api/preview/notification-email
    /// Saves the caller's notification email for Private Preview approval.
    /// AuthenticatedUser policy — preview-blocked users can call this.
    /// </summary>
    [Function("SavePreviewNotificationEmail")]
    [Authorize]
    public async Task<HttpResponseData> SaveNotificationEmail(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "preview/notification-email")] HttpRequestData req,
        FunctionContext context)
    {
        var principal = context.GetUser();
        var tenantId = principal?.GetTenantId();

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteAsJsonAsync(new { error = "Could not determine tenant" });
            return bad;
        }

        var body = await req.ReadFromJsonAsync<SaveNotificationEmailRequest>();
        var email = body?.Email?.Trim();

        if (!string.IsNullOrEmpty(email) && !email.Contains('@'))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteAsJsonAsync(new { error = "Invalid email address" });
            return bad;
        }

        await _previewWhitelistService.SaveNotificationEmailAsync(tenantId, email);

        _logger.LogInformation(
            "Preview notification email updated for tenant {TenantId}: {Email}",
            tenantId, string.IsNullOrEmpty(email) ? "(cleared)" : email);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "Notification email saved", email });
        return response;
    }

    /// <summary>
    /// POST /api/preview/send-welcome-email/{tenantId}
    /// Sends (or resends) the Private Preview welcome email. Global Admin only.
    /// Accepts optional { email } in body — if provided, saves it to PreviewWhitelist table before sending.
    /// </summary>
    [Function("SendPreviewWelcomeEmail")]
    [Authorize]
    public async Task<HttpResponseData> SendWelcomeEmail(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "preview/send-welcome-email/{tenantId}")] HttpRequestData req,
        string tenantId,
        FunctionContext context)
    {
        // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware

        // If the caller provides an email in the body, save it first
        var body = await req.ReadFromJsonAsync<SaveNotificationEmailRequest>();
        var bodyEmail = body?.Email?.Trim();
        if (!string.IsNullOrWhiteSpace(bodyEmail))
        {
            await _previewWhitelistService.SaveNotificationEmailAsync(tenantId, bodyEmail);
        }

        var email = !string.IsNullOrWhiteSpace(bodyEmail)
            ? bodyEmail
            : await _previewWhitelistService.GetNotificationEmailAsync(tenantId);

        if (string.IsNullOrWhiteSpace(email))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteAsJsonAsync(new { error = "No notification email configured for this tenant" });
            return bad;
        }

        var tenantConfig = await _tenantConfigurationService.GetConfigurationAsync(tenantId);
        await _resendEmailService.SendPreviewApprovedEmailAsync(email, tenantConfig.DomainName);

        var principal = context.GetUser();
        var upn = principal?.GetUserPrincipalName();
        _logger.LogInformation(
            "Welcome email sent to {Email} for tenant {TenantId} by {Upn}",
            email, tenantId, upn);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "Welcome email sent", email });
        return response;
    }
}

public class SaveNotificationEmailRequest
{
    public string Email { get; set; } = string.Empty;
}
