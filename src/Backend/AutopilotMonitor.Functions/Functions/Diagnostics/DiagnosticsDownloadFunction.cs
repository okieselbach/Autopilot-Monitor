using System.Diagnostics;
using System.Net;
using System.Web;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Diagnostics;
using Azure.Storage.Blobs;
using Microsoft.ApplicationInsights;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Diagnostics
{
    /// <summary>
    /// Proxies a diagnostics blob download through the backend. Two destinations are
    /// supported and routed by the shape of the blob name:
    /// <list type="bullet">
    ///   <item><c>CustomerSas</c>: blob name has no slash → blob lives in the customer's
    ///         storage account; we build the URL from the tenant's container SAS and
    ///         stream it. SAS never leaves the server.</item>
    ///   <item><c>Hosted</c>: blob name is <c>{tenantId}/{filename}</c> → blob lives in
    ///         the backend's own storage. We stream it directly via the connection
    ///         string; no SAS construction. The leading <c>{tenantId}</c> segment must
    ///         equal the requesting tenantId (defence-in-depth on top of the existing
    ///         <c>TenantScoping</c> policy middleware).</item>
    /// </list>
    ///
    /// Emits a "DiagnosticsDownloadProxied" custom event to Application Insights
    /// with blob size and duration metrics for traffic monitoring.
    /// </summary>
    public class DiagnosticsDownloadFunction
    {
        private readonly ILogger<DiagnosticsDownloadFunction> _logger;
        private readonly TenantConfigurationService _configService;
        private readonly AdminConfigurationService _adminConfigService;
        private readonly TelemetryClient _telemetryClient;
        private readonly HostedDiagnosticsBlobService _hostedDiagnostics;

        public DiagnosticsDownloadFunction(
            ILogger<DiagnosticsDownloadFunction> logger,
            TenantConfigurationService configService,
            AdminConfigurationService adminConfigService,
            TelemetryClient telemetryClient,
            HostedDiagnosticsBlobService hostedDiagnostics)
        {
            _logger = logger;
            _configService = configService;
            _adminConfigService = adminConfigService;
            _telemetryClient = telemetryClient;
            _hostedDiagnostics = hostedDiagnostics;
        }

        [Function("DiagnosticsDownloadUrl")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "diagnostics/download-url")] HttpRequestData req)
        {
            try
            {
                // Authentication, MemberRead authz, AND cross-tenant access enforced by
                // PolicyEnforcementMiddleware (catalog: TenantScoping.QueryParam).
                // requestCtx.TargetTenantId is the middleware-validated tenantId from the
                // ?tenantId= query param (GA bypass already applied).
                var requestCtx = req.GetRequestContext();

                var query = HttpUtility.ParseQueryString(req.Url.Query);
                var rawBlobName = query["blobName"];

                if (string.IsNullOrEmpty(query["tenantId"]) || string.IsNullOrEmpty(rawBlobName))
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { success = false, message = "tenantId and blobName query parameters are required." });
                    return badRequest;
                }

                var tenantId = requestCtx.TargetTenantId;

                // Classify + validate the blob name. Hosted paths carry the tenant prefix;
                // CustomerSas blobs sit at the container root. Shape-based routing avoids
                // an extra Session-row lookup for every download and keeps the API
                // contract unchanged.
                var (destination, classifyErr) = ClassifyBlobName(rawBlobName, tenantId);
                if (classifyErr != null)
                {
                    _logger.LogWarning(
                        "DiagnosticsDownload: rejecting blob {Blob} for tenant {TenantId}: {Reason}",
                        rawBlobName, tenantId, classifyErr);
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { success = false, message = "Invalid blob name." });
                    return badRequest;
                }

                if (destination == BlobDestination.Hosted)
                {
                    return await ServeHostedAsync(req, requestCtx, tenantId, rawBlobName!);
                }
                return await ServeCustomerSasAsync(req, requestCtx, tenantId, rawBlobName!);
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogWarning("DiagnosticsDownload: Blob not found for requested download");
                var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
                await notFoundResponse.WriteAsJsonAsync(new { success = false, message = "Diagnostics package not found." });
                return notFoundResponse;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("DiagnosticsDownload: Operation timed out or was cancelled for requested blob download");
                var timeoutResponse = req.CreateResponse(HttpStatusCode.GatewayTimeout);
                await timeoutResponse.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "Diagnostics download timed out. The file may be too large or the connection is too slow."
                });
                return timeoutResponse;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error proxying diagnostics download");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error." });
                return errorResponse;
            }
        }

        // -------- CustomerSas branch --------

        private async Task<HttpResponseData> ServeCustomerSasAsync(
            HttpRequestData req, RequestContext requestCtx, string tenantId, string blobName)
        {
            var tenantConfig = await _configService.GetConfigurationAsync(tenantId);
            if (string.IsNullOrEmpty(tenantConfig.DiagnosticsBlobSasUrl))
            {
                var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
                await notFoundResponse.WriteAsJsonAsync(new { success = false, message = "No Blob Storage SAS URL configured for this tenant." });
                return notFoundResponse;
            }

            var containerSasUrl = tenantConfig.DiagnosticsBlobSasUrl;
            var questionMarkIndex = containerSasUrl.IndexOf('?');
            string blobUrl;
            if (questionMarkIndex >= 0)
            {
                var basePath = containerSasUrl.Substring(0, questionMarkIndex).TrimEnd('/');
                var queryString = containerSasUrl.Substring(questionMarkIndex);
                blobUrl = $"{basePath}/{blobName}{queryString}";
            }
            else
            {
                blobUrl = $"{containerSasUrl.TrimEnd('/')}/{blobName}";
            }

            var adminConfig = await _adminConfigService.GetConfigurationAsync();
            var maxSizeBytes = (long)adminConfig.MaxDiagnosticsDownloadSizeMB * 1024 * 1024;
            var timeoutSeconds = adminConfig.DiagnosticsDownloadTimeoutSeconds;

            using var cts = timeoutSeconds > 0
                ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds))
                : new CancellationTokenSource();

            var sw = Stopwatch.StartNew();
            var blobClient = new BlobClient(new Uri(blobUrl));
            var download = await blobClient.DownloadStreamingAsync(cancellationToken: cts.Token);
            sw.Stop();

            return await StreamWithSizeGuardAsync(
                req, requestCtx, tenantId, blobName, "CustomerSas",
                download.Value.Details.ContentLength, download.Value.Content,
                sw.ElapsedMilliseconds, maxSizeBytes, adminConfig.MaxDiagnosticsDownloadSizeMB, cts.Token);
        }

        // -------- Hosted branch --------

        private async Task<HttpResponseData> ServeHostedAsync(
            HttpRequestData req, RequestContext requestCtx, string tenantId, string blobName)
        {
            var adminConfig = await _adminConfigService.GetConfigurationAsync();
            var maxSizeBytes = (long)adminConfig.MaxDiagnosticsDownloadSizeMB * 1024 * 1024;
            var timeoutSeconds = adminConfig.DiagnosticsDownloadTimeoutSeconds;

            using var cts = timeoutSeconds > 0
                ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds))
                : new CancellationTokenSource();

            var sw = Stopwatch.StartNew();
            var download = await _hostedDiagnostics.OpenReadAsync(blobName, cts.Token);
            sw.Stop();

            return await StreamWithSizeGuardAsync(
                req, requestCtx, tenantId, blobName, "Hosted",
                download.Value.Details.ContentLength, download.Value.Content,
                sw.ElapsedMilliseconds, maxSizeBytes, adminConfig.MaxDiagnosticsDownloadSizeMB, cts.Token);
        }

        // -------- Shared streaming + size-guard --------

        private async Task<HttpResponseData> StreamWithSizeGuardAsync(
            HttpRequestData req, RequestContext requestCtx,
            string tenantId, string blobName, string destination,
            long contentLength, Stream content,
            long fetchElapsedMs, long maxSizeBytes, int maxSizeMb,
            CancellationToken ct)
        {
            // Enforce size limit before streaming (fast reject)
            if (maxSizeBytes > 0 && contentLength > maxSizeBytes)
            {
                _logger.LogWarning(
                    "DiagnosticsDownload: Blob {BlobName} for tenant {TenantId} (destination={Destination}) rejected — size {SizeBytes} bytes exceeds limit {MaxSizeBytes} bytes",
                    blobName, tenantId, destination, contentLength, maxSizeBytes);

                content.Dispose();

                var tooLarge = req.CreateResponse(HttpStatusCode.RequestEntityTooLarge);
                await tooLarge.WriteAsJsonAsync(new
                {
                    success = false,
                    message = $"Diagnostics package size ({contentLength / (1024 * 1024)} MB) exceeds the maximum allowed size ({maxSizeMb} MB)."
                });
                return tooLarge;
            }

            _logger.LogInformation(
                "DiagnosticsDownload: Proxying blob {BlobName} for tenant {TenantId} (destination={Destination}), size {SizeBytes} bytes, fetch took {DurationMs}ms",
                blobName, tenantId, destination, contentLength, fetchElapsedMs);

            // Track custom event for analytics; carries destination so we can split
            // App Insights queries by store later (e.g. cost analysis on hosted).
            _telemetryClient.TrackEvent("DiagnosticsDownloadProxied",
                properties: new Dictionary<string, string>
                {
                    ["TenantId"] = tenantId,
                    ["BlobName"] = blobName,
                    ["Destination"] = destination,
                    ["UserId"] = requestCtx.UserPrincipalName,
                    ["UserRole"] = requestCtx.UserRole
                },
                metrics: new Dictionary<string, double>
                {
                    ["BlobSizeBytes"] = contentLength,
                    ["DurationMs"] = fetchElapsedMs
                });

            // Use only the trailing filename for the Content-Disposition so the browser
            // doesn't try to encode the tenant-prefix slash into the suggested name.
            var downloadFilename = ExtractDownloadFilename(blobName);

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/octet-stream");
            response.Headers.Add("Content-Disposition", $"attachment; filename=\"{downloadFilename}\"");
            if (contentLength > 0)
            {
                response.Headers.Add("Content-Length", contentLength.ToString());
            }

            await content.CopyToAsync(response.Body, ct);
            return response;
        }

        private static string ExtractDownloadFilename(string blobName)
        {
            var slashIndex = blobName.LastIndexOf('/');
            return slashIndex >= 0 && slashIndex < blobName.Length - 1
                ? blobName.Substring(slashIndex + 1)
                : blobName;
        }

        // -------- Classification + validation --------

        internal enum BlobDestination { CustomerSas, Hosted }

        /// <summary>
        /// Pure helper: classifies a blob name into <see cref="BlobDestination"/> and
        /// runs validation. Returns the destination + null on success, or a (mostly
        /// for telemetry) reason string when the name should be rejected. Exposed
        /// internal-static so xUnit can pin every branch without spinning up
        /// HttpRequestData.
        /// </summary>
        internal static (BlobDestination Destination, string? Reason) ClassifyBlobName(string? rawBlobName, string tenantId)
        {
            if (string.IsNullOrEmpty(rawBlobName))
                return (BlobDestination.CustomerSas, "empty");
            if (rawBlobName.Contains("..") || rawBlobName.Contains('\0') || rawBlobName.Contains('\\'))
                return (BlobDestination.CustomerSas, "traversal-or-null");

            // Decode once; reject if a second decoding would change the value (double-
            // encoding attack: %252F → %2F → /). One round-trip is allowed so the UI
            // can URL-encode the legitimate Hosted prefix slash as %2F.
            string decoded;
            try { decoded = Uri.UnescapeDataString(rawBlobName); }
            catch { return (BlobDestination.CustomerSas, "undecodable"); }
            string doubleDecoded;
            try { doubleDecoded = Uri.UnescapeDataString(decoded); }
            catch { return (BlobDestination.CustomerSas, "undecodable"); }
            if (doubleDecoded != decoded)
                return (BlobDestination.CustomerSas, "double-encoding");

            if (decoded.Contains('\\') || decoded.Contains("..") || decoded.Contains('\0'))
                return (BlobDestination.CustomerSas, "traversal-or-null-decoded");

            var slashIndex = decoded.IndexOf('/');
            if (slashIndex < 0)
            {
                // No slash → CustomerSas (container-root blob, legacy behaviour).
                return (BlobDestination.CustomerSas, null);
            }

            // Has a slash → Hosted shape: must be exactly {tenantId}/{filename}, with
            // the leading segment matching the requesting tenant (defence-in-depth on
            // top of the cross-tenant policy middleware).
            var prefix = decoded.Substring(0, slashIndex);
            var rest = decoded.Substring(slashIndex + 1);
            if (string.IsNullOrEmpty(prefix) || string.IsNullOrEmpty(rest))
                return (BlobDestination.Hosted, "empty-segment");
            if (!string.Equals(prefix, tenantId, StringComparison.Ordinal))
                return (BlobDestination.Hosted, "tenant-prefix-mismatch");
            if (rest.Contains('/'))
                return (BlobDestination.Hosted, "nested-path");

            return (BlobDestination.Hosted, null);
        }
    }
}
