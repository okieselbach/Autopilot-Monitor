using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions
{
    public class UpdateTenantSecurityBypassFunction
    {
        private readonly ILogger<UpdateTenantSecurityBypassFunction> _logger;
        private readonly TenantConfigurationService _configService;
        private readonly GalacticAdminService _galacticAdminService;

        public UpdateTenantSecurityBypassFunction(
            ILogger<UpdateTenantSecurityBypassFunction> logger,
            TenantConfigurationService configService,
            GalacticAdminService galacticAdminService)
        {
            _logger = logger;
            _configService = configService;
            _galacticAdminService = galacticAdminService;
        }

        [Function("UpdateTenantSecurityBypass")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "config/{tenantId}/security-bypass")] HttpRequestData req,
            string tenantId)
        {
            try
            {
                if (!TenantHelper.IsAuthenticated(req))
                {
                    var unauthorized = req.CreateResponse(HttpStatusCode.Unauthorized);
                    await unauthorized.WriteAsJsonAsync(new { error = "Authentication required." });
                    return unauthorized;
                }

                var userIdentifier = TenantHelper.GetUserIdentifier(req);
                var isGalacticAdmin = await _galacticAdminService.IsGalacticAdminAsync(userIdentifier);
                if (!isGalacticAdmin)
                {
                    var forbidden = req.CreateResponse(HttpStatusCode.Forbidden);
                    await forbidden.WriteAsJsonAsync(new { error = "Access denied. Galactic Admin required." });
                    return forbidden;
                }

                var body = await req.ReadFromJsonAsync<UpdateBypassRequest>();
                if (body == null)
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteAsJsonAsync(new { error = "Invalid request body." });
                    return bad;
                }

                var config = await _configService.GetConfigurationAsync(tenantId);
                config.AllowInsecureAgentRequests = body.AllowInsecureAgentRequests;
                config.UpdatedBy = userIdentifier;
                await _configService.SaveConfigurationAsync(config);

                _logger.LogWarning(
                    "Galactic Admin {User} set AllowInsecureAgentRequests={Enabled} for tenant {TenantId}",
                    userIdentifier,
                    body.AllowInsecureAgentRequests,
                    tenantId);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    tenantId,
                    allowInsecureAgentRequests = config.AllowInsecureAgentRequests,
                    message = config.AllowInsecureAgentRequests
                        ? "Security bypass enabled for test purposes."
                        : "Security bypass disabled."
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating security bypass for tenant {TenantId}", tenantId);
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { error = "Internal server error" });
                return response;
            }
        }

        public class UpdateBypassRequest
        {
            public bool AllowInsecureAgentRequests { get; set; }
        }
    }
}
