using System.Net;
using System.Web;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions
{
    /// <summary>
    /// Returns a download URL for a diagnostics package stored in the tenant's Blob Storage.
    /// Constructs the full blob URL from the tenant's configured Container SAS URL + blob name.
    /// </summary>
    public class DiagnosticsDownloadFunction
    {
        private readonly ILogger<DiagnosticsDownloadFunction> _logger;
        private readonly TenantConfigurationService _configService;
        private readonly GalacticAdminService _galacticAdminService;

        public DiagnosticsDownloadFunction(
            ILogger<DiagnosticsDownloadFunction> logger,
            TenantConfigurationService configService,
            GalacticAdminService galacticAdminService)
        {
            _logger = logger;
            _configService = configService;
            _galacticAdminService = galacticAdminService;
        }

        [Function("DiagnosticsDownloadUrl")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "diagnostics/download-url")] HttpRequestData req)
        {
            try
            {
                // Validate authentication
                if (!TenantHelper.IsAuthenticated(req))
                {
                    var unauthorizedResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                    await unauthorizedResponse.WriteAsJsonAsync(new { success = false, message = "Authentication required." });
                    return unauthorizedResponse;
                }

                var userTenantId = TenantHelper.GetTenantId(req);
                var userIdentifier = TenantHelper.GetUserIdentifier(req);

                var query = HttpUtility.ParseQueryString(req.Url.Query);
                var tenantId = query["tenantId"];
                var blobName = query["blobName"];

                if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(blobName))
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { success = false, message = "tenantId and blobName query parameters are required." });
                    return badRequest;
                }

                // Validate tenant access
                if (tenantId != userTenantId)
                {
                    var isGalacticAdmin = await _galacticAdminService.IsGalacticAdminAsync(userIdentifier);
                    if (!isGalacticAdmin)
                    {
                        var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
                        await forbiddenResponse.WriteAsJsonAsync(new { success = false, message = "Access denied." });
                        return forbiddenResponse;
                    }
                }

                // Validate blob name (prevent path traversal)
                if (blobName.Contains("..") || blobName.Contains("/") || blobName.Contains("\\"))
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { success = false, message = "Invalid blob name." });
                    return badRequest;
                }

                // Get tenant config to retrieve SAS URL
                var tenantConfig = await _configService.GetConfigurationAsync(tenantId);
                if (string.IsNullOrEmpty(tenantConfig.DiagnosticsBlobSasUrl))
                {
                    var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFoundResponse.WriteAsJsonAsync(new { success = false, message = "No Blob Storage SAS URL configured for this tenant." });
                    return notFoundResponse;
                }

                // Construct full blob download URL from container SAS + blob name
                var containerSasUrl = tenantConfig.DiagnosticsBlobSasUrl;
                var questionMarkIndex = containerSasUrl.IndexOf('?');
                string downloadUrl;
                if (questionMarkIndex >= 0)
                {
                    var basePath = containerSasUrl.Substring(0, questionMarkIndex).TrimEnd('/');
                    var queryString = containerSasUrl.Substring(questionMarkIndex);
                    downloadUrl = $"{basePath}/{blobName}{queryString}";
                }
                else
                {
                    downloadUrl = $"{containerSasUrl.TrimEnd('/')}/{blobName}";
                }

                _logger.LogInformation($"Generated diagnostics download URL for tenant {tenantId}, blob {blobName}");

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new { success = true, downloadUrl });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating diagnostics download URL");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error." });
                return errorResponse;
            }
        }
    }
}
