using System.Net;
using System.Web;
using Azure.Storage.Blobs;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Reports
{
    /// <summary>
    /// Returns a short-lived SAS download URL for a session report blob stored in central blob storage.
    /// Galactic Admin only.
    /// </summary>
    public class GetSessionReportDownloadUrlFunction
    {
        private readonly ILogger<GetSessionReportDownloadUrlFunction> _logger;
        private readonly GalacticAdminService _galacticAdminService;
        private readonly string _blobConnectionString;
        private const string ContainerName = "session-reports";

        public GetSessionReportDownloadUrlFunction(
            ILogger<GetSessionReportDownloadUrlFunction> logger,
            GalacticAdminService galacticAdminService,
            IConfiguration configuration)
        {
            _logger = logger;
            _galacticAdminService = galacticAdminService;
            _blobConnectionString = configuration["AzureBlobStorageConnectionString"]
                ?? throw new InvalidOperationException("AzureBlobStorageConnectionString is not configured");
        }

        [Function("GetSessionReportDownloadUrl")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "galactic/session-reports/download-url")] HttpRequestData req)
        {
            try
            {
                if (!TenantHelper.IsAuthenticated(req))
                {
                    var unauthorizedResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                    await unauthorizedResponse.WriteAsJsonAsync(new { success = false, message = "Authentication required." });
                    return unauthorizedResponse;
                }

                var userIdentifier = TenantHelper.GetUserIdentifier(req);
                var isGalacticAdmin = await _galacticAdminService.IsGalacticAdminAsync(userIdentifier);
                if (!isGalacticAdmin)
                {
                    var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
                    await forbiddenResponse.WriteAsJsonAsync(new { success = false, message = "Access denied. Galactic Admin role required." });
                    return forbiddenResponse;
                }

                var query = HttpUtility.ParseQueryString(req.Url.Query);
                var blobName = query["blobName"];

                if (string.IsNullOrEmpty(blobName))
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { success = false, message = "blobName query parameter is required." });
                    return badRequest;
                }

                // Prevent path traversal
                if (blobName.Contains("..") || blobName.Contains("/") || blobName.Contains("\\"))
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { success = false, message = "Invalid blob name." });
                    return badRequest;
                }

                var blobServiceClient = new BlobServiceClient(_blobConnectionString);
                var containerClient = blobServiceClient.GetBlobContainerClient(ContainerName);
                var blobClient = containerClient.GetBlobClient(blobName);

                if (!await blobClient.ExistsAsync())
                {
                    var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFoundResponse.WriteAsJsonAsync(new { success = false, message = "Blob not found." });
                    return notFoundResponse;
                }

                // The connection string uses a service-level SAS token, so the blob URI
                // already contains the access token — no separate SAS generation needed.
                var downloadUrl = blobClient.Uri.ToString();

                _logger.LogInformation("Generated session report download URL for blob {BlobName}", blobName);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new { success = true, downloadUrl });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating session report download URL");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error." });
                return errorResponse;
            }
        }
    }
}
