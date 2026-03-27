using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using System.Collections.Generic;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Functions.Config
{
    public class UpdateTenantConfigurationFunction
    {
        private readonly ILogger<UpdateTenantConfigurationFunction> _logger;
        private readonly TenantConfigurationService _configService;
        private readonly IMaintenanceRepository _maintenanceRepo;

        public UpdateTenantConfigurationFunction(
            ILogger<UpdateTenantConfigurationFunction> logger,
            TenantConfigurationService configService,
            IMaintenanceRepository maintenanceRepo)
        {
            _logger = logger;
            _configService = configService;
            _maintenanceRepo = maintenanceRepo;
        }

        [Function("UpdateTenantConfiguration")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "put", "post", Route = "config/{tenantId}")] HttpRequestData req,
            string tenantId)
        {
            try
            {
                // Authentication + TenantAdminOrGA authorization enforced by PolicyEnforcementMiddleware
                var requestCtx = req.GetRequestContext();
                var userIdentifier = requestCtx.UserPrincipalName;

                // Validate tenant access: cross-tenant only for Global Admins
                if (!requestCtx.IsGlobalAdmin && !string.Equals(requestCtx.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("User {User} from tenant {AuthTenant} attempted to update configuration for tenant {TargetTenant}",
                        userIdentifier, requestCtx.TenantId, tenantId);
                    var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
                    await forbiddenResponse.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Access denied. You can only update your own tenant's configuration."
                    });
                    return forbiddenResponse;
                }

                _logger.LogInformation("UpdateTenantConfiguration: {TenantId} by user {User}", tenantId, userIdentifier);

                // Parse request body
                if (req.Headers.TryGetValues("Content-Length", out var clValues)
                    && long.TryParse(clValues.FirstOrDefault(), out var contentLength)
                    && contentLength > 1_048_576) // 1 MB limit
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { success = false, message = "Request body too large" });
                    return badRequest;
                }
                var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                var config = JsonConvert.DeserializeObject<TenantConfiguration>(requestBody);

                if (config == null)
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { error = "Invalid configuration" });
                    return badRequest;
                }

                // Ensure tenant ID matches
                config.TenantId = tenantId;

                // Set the actual user identifier for audit logging
                config.UpdatedBy = userIdentifier;

                // Protect GA-only fields from non-Global-Admin callers
                var existingConfig = await _configService.GetConfigurationAsync(tenantId);
                if (!requestCtx.IsGlobalAdmin)
                {
                    if (config.AllowInsecureAgentRequests != existingConfig.AllowInsecureAgentRequests ||
                        config.BootstrapTokenEnabled != existingConfig.BootstrapTokenEnabled ||
                        config.UnrestrictedModeEnabled != existingConfig.UnrestrictedModeEnabled ||
                        config.CustomRateLimitRequestsPerMinute != existingConfig.CustomRateLimitRequestsPerMinute ||
                        config.RateLimitRequestsPerMinute != existingConfig.RateLimitRequestsPerMinute ||
                        config.Disabled != existingConfig.Disabled)
                    {
                        _logger.LogWarning(
                            "Tenant Admin {User} attempted to modify GA-only fields for tenant {TenantId}",
                            userIdentifier, tenantId);
                    }

                    config.AllowInsecureAgentRequests = existingConfig.AllowInsecureAgentRequests;
                    config.BootstrapTokenEnabled = existingConfig.BootstrapTokenEnabled;
                    config.UnrestrictedModeEnabled = existingConfig.UnrestrictedModeEnabled;
                    config.CustomRateLimitRequestsPerMinute = existingConfig.CustomRateLimitRequestsPerMinute;
                    config.RateLimitRequestsPerMinute = existingConfig.RateLimitRequestsPerMinute;
                    config.Disabled = existingConfig.Disabled;
                    config.DisabledReason = existingConfig.DisabledReason;
                    config.DisabledUntil = existingConfig.DisabledUntil;
                }

                // Safety: if GA gate is off, force UnrestrictedMode to false
                if (!config.UnrestrictedModeEnabled)
                {
                    config.UnrestrictedMode = false;
                }

                // MaxNdjsonPayloadSizeMB is table-only — always preserve existing value
                config.MaxNdjsonPayloadSizeMB = existingConfig.MaxNdjsonPayloadSizeMB;

                // Save configuration
                await _configService.SaveConfigurationAsync(config);

                var changes = ConfigDiffHelper.GetChanges(existingConfig, config);
                await _maintenanceRepo.LogAuditEntryAsync(
                    tenantId,
                    "UPDATE",
                    "TenantConfiguration",
                    tenantId,
                    userIdentifier,
                    changes.Count > 0 ? changes : null
                );

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    message = "Configuration updated successfully",
                    config = config
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error updating configuration for tenant {tenantId}");
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { error = "Internal server error" });
                return response;
            }
        }
    }
}
