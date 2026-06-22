using System;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Config
{
    public class GetTenantConfigurationFunction
    {
        private readonly ILogger<GetTenantConfigurationFunction> _logger;
        private readonly TenantConfigurationService _configService;

        public GetTenantConfigurationFunction(
            ILogger<GetTenantConfigurationFunction> logger,
            TenantConfigurationService configService)
        {
            _logger = logger;
            _configService = configService;
        }

        [Function("GetTenantConfiguration")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "config/{tenantId}")] HttpRequestData req,
            string tenantId)
        {
            try
            {
                // Authentication + TenantAdminOrGA authorization enforced by PolicyEnforcementMiddleware
                var requestCtx = req.GetRequestContext();
                var userIdentifier = requestCtx.UserPrincipalName;

                _logger.LogInformation($"GetTenantConfiguration: {requestCtx.TargetTenantId} by user {userIdentifier}");

                var config = await _configService.GetConfigurationAsync(requestCtx.TargetTenantId);

                // Tenant admins (own tenant) and Global Admins get the full config including
                // DiagnosticsBlobSasUrl / webhook URLs / custom headers — they manage those in the
                // Settings UI. Secrets are redacted ONLY for the read-only-reader view (returns a copy;
                // the cached instance is never mutated). ADDITIVE: a GlobalReader who is ALSO this
                // tenant's own Admin is acting as the tenant admin here, so they keep the FULL config —
                // redaction applies only when they are NOT the own-tenant admin (i.e. pure reader, or a
                // reader viewing a DIFFERENT tenant cross-tenant). This also prevents a "***REDACTED***"
                // placeholder from ever reaching the Settings save round-trip for an own-tenant admin.
                var ownTenantAdminView = requestCtx.IsTenantAdmin
                    && string.Equals(requestCtx.TargetTenantId, requestCtx.TenantId, StringComparison.OrdinalIgnoreCase);
                if (requestCtx.IsGlobalReader && !ownTenantAdminView)
                    config = config.RedactedCopyForReader();

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(config);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting configuration for tenant {TenantId}", tenantId);
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { error = "Internal server error" });
                return response;
            }
        }
    }
}
