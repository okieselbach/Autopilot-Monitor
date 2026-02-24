using System.Net;
using System.Web;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions
{
    /// <summary>
    /// Returns a short-lived SAS URL for diagnostics package upload.
    /// Called by the agent just before uploading diagnostics — not at startup.
    /// The SAS URL is never cached in the agent's config, reducing its exposure window.
    /// Uses device authentication (client certificate), not JWT.
    /// </summary>
    public class GetDiagnosticsUploadUrlFunction
    {
        private readonly ILogger<GetDiagnosticsUploadUrlFunction> _logger;
        private readonly TenantConfigurationService _configService;
        private readonly RateLimitService _rateLimitService;
        private readonly AutopilotDeviceValidator _autopilotDeviceValidator;

        public GetDiagnosticsUploadUrlFunction(
            ILogger<GetDiagnosticsUploadUrlFunction> logger,
            TenantConfigurationService configService,
            RateLimitService rateLimitService,
            AutopilotDeviceValidator autopilotDeviceValidator)
        {
            _logger = logger;
            _configService = configService;
            _rateLimitService = rateLimitService;
            _autopilotDeviceValidator = autopilotDeviceValidator;
        }

        [Function("GetDiagnosticsUploadUrl")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "diagnostics/upload-url")] HttpRequestData req)
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
                    _logger
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
