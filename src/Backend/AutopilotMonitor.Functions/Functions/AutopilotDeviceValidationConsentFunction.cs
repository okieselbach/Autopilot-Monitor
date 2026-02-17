using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions;

public class AutopilotDeviceValidationConsentFunction
{
    private readonly ILogger<AutopilotDeviceValidationConsentFunction> _logger;
    private readonly IConfiguration _configuration;
    private readonly TenantAdminsService _tenantAdminsService;
    private readonly GalacticAdminService _galacticAdminService;
    private readonly AutopilotDeviceValidator _autopilotDeviceValidator;

    public AutopilotDeviceValidationConsentFunction(
        ILogger<AutopilotDeviceValidationConsentFunction> logger,
        IConfiguration configuration,
        TenantAdminsService tenantAdminsService,
        GalacticAdminService galacticAdminService,
        AutopilotDeviceValidator autopilotDeviceValidator)
    {
        _logger = logger;
        _configuration = configuration;
        _tenantAdminsService = tenantAdminsService;
        _galacticAdminService = galacticAdminService;
        _autopilotDeviceValidator = autopilotDeviceValidator;
    }

    [Function("GetAutopilotDeviceValidationConsentUrl")]
    public async Task<HttpResponseData> GetConsentUrl(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "config/{tenantId}/autopilot-device-validation/consent-url")] HttpRequestData req,
        string tenantId)
    {
        var authError = await EnsureAuthorizedTenantAdminAsync(req, tenantId);
        if (authError != null)
        {
            return authError;
        }

        var validatorClientId = _configuration["EntraId:ClientId"];
        if (string.IsNullOrWhiteSpace(validatorClientId))
        {
            var badConfig = req.CreateResponse(HttpStatusCode.InternalServerError);
            await badConfig.WriteAsJsonAsync(new
            {
                error = "Validator app client ID is not configured on the backend."
            });
            return badConfig;
        }

        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var redirectUri = query["redirectUri"];
        if (string.IsNullOrWhiteSpace(redirectUri))
        {
            redirectUri = $"{req.Url.Scheme}://{req.Url.Authority}/settings";
        }

        var consentUrl =
            $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenantId)}/adminconsent" +
            $"?client_id={Uri.EscapeDataString(validatorClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&state={Uri.EscapeDataString("autopilot-device-validation-enable")}";

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            consentUrl
        });
        return response;
    }

    [Function("GetAutopilotDeviceValidationConsentStatus")]
    public async Task<HttpResponseData> GetConsentStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "config/{tenantId}/autopilot-device-validation/consent-status")] HttpRequestData req,
        string tenantId)
    {
        var authError = await EnsureAuthorizedTenantAdminAsync(req, tenantId);
        if (authError != null)
        {
            return authError;
        }

        var result = await _autopilotDeviceValidator.GetConsentStatusAsync(tenantId);
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            isConsented = result.IsConsented,
            message = result.Message
        });
        return response;
    }

    private async Task<HttpResponseData?> EnsureAuthorizedTenantAdminAsync(HttpRequestData req, string tenantId)
    {
        if (!TenantHelper.IsAuthenticated(req))
        {
            var unauthorized = req.CreateResponse(HttpStatusCode.Unauthorized);
            await unauthorized.WriteAsJsonAsync(new { error = "Authentication required." });
            return unauthorized;
        }

        var authenticatedTenantId = TenantHelper.GetTenantId(req);
        var userIdentifier = TenantHelper.GetUserIdentifier(req);

        if (!string.Equals(authenticatedTenantId, tenantId, StringComparison.OrdinalIgnoreCase))
        {
            var forbidden = req.CreateResponse(HttpStatusCode.Forbidden);
            await forbidden.WriteAsJsonAsync(new { error = "Access denied. You can only manage your own tenant." });
            return forbidden;
        }

        var isGalacticAdmin = await _galacticAdminService.IsGalacticAdminAsync(userIdentifier);
        var isTenantAdmin = await _tenantAdminsService.IsTenantAdminAsync(tenantId, userIdentifier);

        if (!isGalacticAdmin && !isTenantAdmin)
        {
            _logger.LogWarning(
                "User {User} attempted autopilot-device-validation consent operation without admin rights for tenant {TenantId}",
                userIdentifier,
                tenantId);

            var forbidden = req.CreateResponse(HttpStatusCode.Forbidden);
            await forbidden.WriteAsJsonAsync(new
            {
                error = "Access denied. Tenant Admin or Galactic Admin required."
            });
            return forbidden;
        }

        return null;
    }
}
