using System.Net;
using AutopilotMonitor.Functions.Extensions;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Infrastructure;

/// <summary>
/// Authentication and authorization endpoints
/// </summary>
public class AuthFunction
{
    private readonly ILogger<AuthFunction> _logger;
    private readonly GlobalAdminService _globalAdminService;
    private readonly TenantConfigurationService _tenantConfigService;
    private readonly TenantAdminsService _tenantAdminsService;
    private readonly IMetricsRepository _metricsRepo;
    private readonly PreviewWhitelistService _previewWhitelistService;
    private readonly TelegramNotificationService _telegramNotificationService;
    private readonly GlobalNotificationService _globalNotificationService;

    public AuthFunction(
        ILogger<AuthFunction> logger,
        GlobalAdminService globalAdminService,
        TenantConfigurationService tenantConfigService,
        TenantAdminsService tenantAdminsService,
        IMetricsRepository metricsRepo,
        PreviewWhitelistService previewWhitelistService,
        TelegramNotificationService telegramNotificationService,
        GlobalNotificationService globalNotificationService)
    {
        _logger = logger;
        _globalAdminService = globalAdminService;
        _tenantConfigService = tenantConfigService;
        _tenantAdminsService = tenantAdminsService;
        _metricsRepo = metricsRepo;
        _previewWhitelistService = previewWhitelistService;
        _telegramNotificationService = telegramNotificationService;
        _globalNotificationService = globalNotificationService;
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

                // Notify via Telegram that a new tenant has signed up for Private Preview
                _ = _telegramNotificationService.SendNewTenantSignupAsync(tenantId, upn)
                    .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException,
                        "Fire-and-forget Telegram notification failed for tenant {TenantId}", tenantId),
                        TaskContinuationOptions.OnlyOnFaulted);

                // Persistent in-app notification for Global Admins — best effort
                _ = _globalNotificationService.CreateNotificationAsync(
                    "preview_signup",
                    "New Preview Signup",
                    $"Tenant {tenantId} ({domain}), UPN: {upn}");
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

        // Auto-re-enable: if Disabled was true but DisabledUntil has expired, persist the re-enable
        if (tenantConfig.Disabled && !tenantConfig.IsCurrentlyDisabled())
        {
            _logger.LogInformation($"Tenant {tenantId} auto-re-enabled: DisabledUntil ({tenantConfig.DisabledUntil:o}) has expired");
            tenantConfig.Disabled = false;
            tenantConfig.DisabledReason = null;
            tenantConfig.DisabledUntil = null;
            tenantConfig.UpdatedBy = "System (auto-re-enable)";
            await _tenantConfigService.SaveConfigurationAsync(tenantConfig);
        }

        // Check if user is global admin (must happen before preview gate)
        var isGlobalAdmin = await _globalAdminService.IsGlobalAdminAsync(upn);

        // Preview gate: only approved tenants get full portal access.
        // Global Admins bypass the gate (they need access to manage the whitelist).
        if (!isGlobalAdmin && !await _previewWhitelistService.IsApprovedAsync(tenantId))
        {
            _logger.LogInformation("Tenant {TenantId} blocked by preview gate (user: {Upn})", tenantId, upn);

            var previewResponse = req.CreateResponse(HttpStatusCode.Forbidden);
            await previewResponse.WriteAsJsonAsync(new
            {
                error = "PrivatePreview",
                message = "Autopilot Monitor is currently in Private Preview. Your organization is on the waitlist \u2014 we'll notify you when access is granted."
            });
            return previewResponse;
        }

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

        // Get role info for the user (Admin, Operator, Viewer, or null if not a member)
        var memberRole = await _tenantAdminsService.GetMemberRoleAsync(tenantId, upn);
        string? role = memberRole?.Role;
        bool canManageBootstrapTokens = memberRole?.CanManageBootstrapTokens ?? false;

        // Record user login activity for metrics tracking
        _ = _metricsRepo.RecordUserLoginAsync(tenantId, upn, displayName, objectId)
            .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException, "Fire-and-forget RecordUserLoginAsync failed"), TaskContinuationOptions.OnlyOnFaulted);

        var userInfo = new
        {
            tenantId,
            upn,
            displayName,
            objectId,
            isGlobalAdmin,
            isTenantAdmin,
            role,
            canManageBootstrapTokens
        };

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(userInfo);
        return response;
    }

    /// <summary>
    /// GET /api/auth/is-global-admin
    /// Checks if the current user is a Global Admin
    /// </summary>
    [Function("IsGlobalAdmin")]
    [Authorize]
    public async Task<HttpResponseData> IsGlobalAdmin(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auth/is-global-admin")] HttpRequestData req,
        FunctionContext context)
    {
        var principal = context.GetUser();
        if (principal == null)
        {
            return req.CreateResponse(HttpStatusCode.Unauthorized);
        }

        var upn = principal.GetUserPrincipalName();
        var isAdmin = await _globalAdminService.IsGlobalAdminAsync(upn);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { isGlobalAdmin = isAdmin, upn });
        return response;
    }

    /// <summary>
    /// GET /api/auth/global-admins
    /// Lists all Global Admins (only accessible by Global Admins)
    /// </summary>
    [Function("GetGlobalAdmins")]
    [Authorize]
    public async Task<HttpResponseData> GetGlobalAdmins(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auth/global-admins")] HttpRequestData req,
        FunctionContext context)
    {
        // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware

        var admins = await _globalAdminService.GetAllGlobalAdminsAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { admins });
        return response;
    }

    /// <summary>
    /// POST /api/auth/global-admins
    /// Adds a new Global Admin (only accessible by existing Global Admins)
    /// </summary>
    [Function("AddGlobalAdmin")]
    [Authorize]
    public async Task<HttpResponseData> AddGlobalAdmin(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/global-admins")] HttpRequestData req,
        FunctionContext context)
    {
        // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
        var principal = context.GetUser();
        var currentUpn = principal?.GetUserPrincipalName();

        // Parse request body
        var body = await req.ReadFromJsonAsync<AddGlobalAdminRequest>();
        if (body == null || string.IsNullOrWhiteSpace(body.Upn))
        {
            var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequestResponse.WriteAsJsonAsync(new { error = "UPN is required" });
            return badRequestResponse;
        }

        var newAdmin = await _globalAdminService.AddGlobalAdminAsync(body.Upn, currentUpn!);

        _logger.LogInformation($"Global Admin added: {body.Upn} by {currentUpn}");

        var response = req.CreateResponse(HttpStatusCode.Created);
        await response.WriteAsJsonAsync(new { admin = newAdmin });
        return response;
    }

    /// <summary>
    /// DELETE /api/auth/global-admins/{upn}
    /// Removes a Global Admin (only accessible by existing Global Admins)
    /// </summary>
    [Function("RemoveGlobalAdmin")]
    [Authorize]
    public async Task<HttpResponseData> RemoveGlobalAdmin(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "auth/global-admins/{upn}")] HttpRequestData req,
        string upn,
        FunctionContext context)
    {
        // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
        var principal = context.GetUser();
        var currentUpn = principal?.GetUserPrincipalName();

        // Prevent self-removal
        if (upn.Equals(currentUpn, StringComparison.OrdinalIgnoreCase))
        {
            var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequestResponse.WriteAsJsonAsync(new { error = "You cannot remove yourself as a Global Admin" });
            return badRequestResponse;
        }

        await _globalAdminService.RemoveGlobalAdminAsync(upn);

        _logger.LogInformation($"Global Admin removed: {upn} by {currentUpn}");

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "Global Admin removed successfully" });
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

public class AddGlobalAdminRequest
{
    public string Upn { get; set; } = string.Empty;
}
