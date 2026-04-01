using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Admin;

public class AutopilotDeviceValidationConsentFunction
{
    private readonly ILogger<AutopilotDeviceValidationConsentFunction> _logger;
    private readonly IConfiguration _configuration;
    private readonly GraphTokenService _graphTokenService;

    public AutopilotDeviceValidationConsentFunction(
        ILogger<AutopilotDeviceValidationConsentFunction> logger,
        IConfiguration configuration,
        GraphTokenService graphTokenService)
    {
        _logger = logger;
        _configuration = configuration;
        _graphTokenService = graphTokenService;
    }

    [Function("GetAutopilotDeviceValidationConsentUrl")]
    public async Task<HttpResponseData> GetConsentUrl(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "config/{tenantId}/autopilot-device-validation/consent-url")] HttpRequestData req,
        string tenantId)
    {
        // Authentication enforced by PolicyEnforcementMiddleware
        var requestCtx = req.GetRequestContext();

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
            $"https://login.microsoftonline.com/{Uri.EscapeDataString(requestCtx.TargetTenantId)}/adminconsent" +
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
        // Authentication enforced by PolicyEnforcementMiddleware
        var requestCtx = req.GetRequestContext();

        var result = await _graphTokenService.GetConsentStatusAsync(requestCtx.TargetTenantId);
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            isConsented = result.IsConsented,
            message = result.Message
        });
        return response;
    }
}
