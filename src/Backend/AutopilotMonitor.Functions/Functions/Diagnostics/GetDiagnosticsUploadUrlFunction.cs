using System.Net;
using System.Web;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Diagnostics
{
    /// <summary>
    /// Returns the tenant's container SAS URL for diagnostics package upload.
    /// Called by the agent just before uploading diagnostics — not at startup.
    /// The SAS URL is never cached in the agent's config, reducing its exposure window.
    /// Uses device authentication (client certificate), not JWT.
    ///
    /// SECURITY NOTE — Accepted risk (container-scope SAS):
    /// The SAS URL returned here is a long-lived, container-scoped token stored in
    /// TenantConfiguration. Generating short-lived blob-scoped tokens would require
    /// Managed Identity delegation on each tenant's storage account, which is not
    /// feasible in a SaaS model where tenants self-configure their storage.
    ///
    /// Mitigations in place:
    /// - Device authentication required (client certificate validated against Intune CA)
    /// - SAS URL never persisted on device (memory-only, discarded after upload)
    /// - SAS URL never logged
    /// - Rate-limited and hardware-whitelisted
    /// - Upload endpoint not accessible to web users (device auth only)
    ///
    /// If a future architecture change makes MI-based delegation feasible,
    /// replace this with BlobStorageService.GetTenantUploadSasAsync() for
    /// blob-scoped, 15-minute User Delegation SAS tokens.
    /// </summary>
    public class GetDiagnosticsUploadUrlFunction
    {
        private readonly ILogger<GetDiagnosticsUploadUrlFunction> _logger;
        private readonly TenantConfigurationService _configService;
        private readonly RateLimitService _rateLimitService;
        private readonly AutopilotDeviceValidator _autopilotDeviceValidator;
        private readonly CorporateIdentifierValidator _corporateIdentifierValidator;
        private readonly DeviceAssociationValidator _deviceAssociationValidator;
        private readonly BootstrapSessionService _bootstrapSessionService;

        public GetDiagnosticsUploadUrlFunction(
            ILogger<GetDiagnosticsUploadUrlFunction> logger,
            TenantConfigurationService configService,
            RateLimitService rateLimitService,
            AutopilotDeviceValidator autopilotDeviceValidator,
            CorporateIdentifierValidator corporateIdentifierValidator,
            DeviceAssociationValidator deviceAssociationValidator,
            BootstrapSessionService bootstrapSessionService)
        {
            _logger = logger;
            _configService = configService;
            _rateLimitService = rateLimitService;
            _autopilotDeviceValidator = autopilotDeviceValidator;
            _corporateIdentifierValidator = corporateIdentifierValidator;
            _deviceAssociationValidator = deviceAssociationValidator;
            _bootstrapSessionService = bootstrapSessionService;
        }

        [Function("GetDiagnosticsUploadUrl")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "agent/upload-url")] HttpRequestData req)
        {
            try
            {
                // Parse request body
                GetDiagnosticsUploadUrlRequest? requestBody;
                try
                {
                    requestBody = await System.Text.Json.JsonSerializer.DeserializeAsync<GetDiagnosticsUploadUrlRequest>(
                        req.Body,
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new GetDiagnosticsUploadUrlResponse
                    {
                        Success = false,
                        Message = "Invalid request body"
                    });
                    return badRequest;
                }

                if (requestBody == null || string.IsNullOrEmpty(requestBody.TenantId))
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new GetDiagnosticsUploadUrlResponse
                    {
                        Success = false,
                        Message = "tenantId is required"
                    });
                    return badRequest;
                }

                // Validate request security (certificate, rate limit, hardware whitelist)
                var (validation, errorResponse) = await req.ValidateSecurityAsync(
                    requestBody.TenantId,
                    _configService,
                    _rateLimitService,
                    _autopilotDeviceValidator,
                    _corporateIdentifierValidator,
                    _logger,
                    bootstrapSessionService: _bootstrapSessionService,
                    deviceAssociationValidator: _deviceAssociationValidator
                );

                if (errorResponse != null)
                {
                    return errorResponse;
                }

                // Get tenant configuration
                var tenantConfig = await _configService.GetConfigurationAsync(requestBody.TenantId);

                if (string.IsNullOrEmpty(tenantConfig.DiagnosticsBlobSasUrl))
                {
                    var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFound.WriteAsJsonAsync(new GetDiagnosticsUploadUrlResponse
                    {
                        Success = false,
                        Message = "Diagnostics storage not configured for this tenant"
                    });
                    return notFound;
                }

                // Parse expiry from the SAS URL's se= parameter
                var sasExpiry = ParseSasExpiry(tenantConfig.DiagnosticsBlobSasUrl);

                // Log the request — but never log the SAS URL itself
                _logger.LogInformation(
                    "GetDiagnosticsUploadUrl: Issuing upload URL for tenant {TenantId}, session {SessionId}, file {FileName}, SAS expires {ExpiresAt}",
                    requestBody.TenantId,
                    requestBody.SessionId,
                    requestBody.FileName,
                    sasExpiry?.ToString("O") ?? "unknown");

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new GetDiagnosticsUploadUrlResponse
                {
                    Success = true,
                    // Container-scoped SAS — accepted risk, see class docstring for rationale
                    UploadUrl = tenantConfig.DiagnosticsBlobSasUrl,
                    ExpiresAt = sasExpiry ?? DateTime.UtcNow.AddHours(1),
                    Message = null
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting diagnostics upload URL");
                var errorResp = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResp.WriteAsJsonAsync(new GetDiagnosticsUploadUrlResponse
                {
                    Success = false,
                    Message = "Internal server error"
                });
                return errorResp;
            }
        }

        /// <summary>
        /// Parses the expiry datetime from the se= query parameter of a SAS URL.
        /// Returns null if the parameter is missing or cannot be parsed.
        /// </summary>
        private static DateTime? ParseSasExpiry(string sasUrl)
        {
            try
            {
                var queryIndex = sasUrl.IndexOf('?');
                if (queryIndex < 0) return null;

                var query = HttpUtility.ParseQueryString(sasUrl.Substring(queryIndex));
                var seValue = query["se"];
                if (string.IsNullOrEmpty(seValue)) return null;

                if (DateTime.TryParse(seValue, null, System.Globalization.DateTimeStyles.RoundtripKind, out var expiry))
                    return expiry;

                return null;
            }
            catch
            {
                return null;
            }
        }
    }
}
