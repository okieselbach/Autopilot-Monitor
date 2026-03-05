using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Functions
{
    /// <summary>
    /// POST /api/bootstrap/sessions — Create a new bootstrap session for OOBE agent deployment.
    /// Requires JWT authentication and TenantAdmin role.
    /// </summary>
    public class CreateBootstrapSessionFunction
    {
        private readonly ILogger<CreateBootstrapSessionFunction> _logger;
        private readonly BootstrapSessionService _bootstrapService;
        private readonly GalacticAdminService _galacticAdminService;
        private readonly TenantAdminsService _tenantAdminsService;

        public CreateBootstrapSessionFunction(
            ILogger<CreateBootstrapSessionFunction> logger,
            BootstrapSessionService bootstrapService,
            GalacticAdminService galacticAdminService,
            TenantAdminsService tenantAdminsService)
        {
            _logger = logger;
            _bootstrapService = bootstrapService;
            _galacticAdminService = galacticAdminService;
            _tenantAdminsService = tenantAdminsService;
        }

        [Function("CreateBootstrapSession")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "bootstrap/sessions")] HttpRequestData req)
        {
            try
            {
                if (!TenantHelper.IsAuthenticated(req))
                {
                    var unauth = req.CreateResponse(HttpStatusCode.Unauthorized);
                    await unauth.WriteAsJsonAsync(new { error = "Authentication required" });
                    return unauth;
                }

                var authenticatedTenantId = TenantHelper.GetTenantId(req);
                var userIdentifier = TenantHelper.GetUserIdentifier(req);

                // Read request body
                string body;
                using (var reader = new StreamReader(req.Body))
                    body = await reader.ReadToEndAsync();

                var request = JsonConvert.DeserializeObject<CreateBootstrapSessionRequest>(body);
                if (request == null)
                {
                    var badReq = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badReq.WriteAsJsonAsync(new { error = "Invalid request body" });
                    return badReq;
                }

                // Use tenant from JWT if not specified in body
                var tenantId = !string.IsNullOrEmpty(request.TenantId) ? request.TenantId : authenticatedTenantId;

                // Tenant boundary check
                if (!string.Equals(authenticatedTenantId, tenantId, StringComparison.OrdinalIgnoreCase))
                {
                    var isGalactic = await _galacticAdminService.IsGalacticAdminAsync(userIdentifier);
                    if (!isGalactic)
                    {
                        var forbidden = req.CreateResponse(HttpStatusCode.Forbidden);
                        await forbidden.WriteAsJsonAsync(new { error = "Access denied: tenant mismatch" });
                        return forbidden;
                    }
                }

                // Admin check
                var isGalacticAdmin = await _galacticAdminService.IsGalacticAdminAsync(userIdentifier);
                var isTenantAdmin = await _tenantAdminsService.IsTenantAdminAsync(tenantId, userIdentifier);
                if (!isGalacticAdmin && !isTenantAdmin)
                {
                    var forbidden = req.CreateResponse(HttpStatusCode.Forbidden);
                    await forbidden.WriteAsJsonAsync(new { error = "Tenant admin access required" });
                    return forbidden;
                }

                // Validate validity hours
                var validityHours = request.ValidityHours > 0 ? request.ValidityHours : 8;

                var session = await _bootstrapService.CreateAsync(tenantId, validityHours, userIdentifier, request.Label);

                var responseData = new CreateBootstrapSessionResponse
                {
                    Success = true,
                    ShortCode = session.ShortCode,
                    BootstrapUrl = $"https://autopilotmonitor.com/go/{session.ShortCode}",
                    ExpiresAt = session.ExpiresAt,
                    Message = $"Bootstrap session created. Valid for {validityHours} hours."
                };

                var response = req.CreateResponse(HttpStatusCode.Created);
                await response.WriteAsJsonAsync(responseData);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating bootstrap session");
                var error = req.CreateResponse(HttpStatusCode.InternalServerError);
                await error.WriteAsJsonAsync(new { error = "Failed to create bootstrap session" });
                return error;
            }
        }
    }
}
