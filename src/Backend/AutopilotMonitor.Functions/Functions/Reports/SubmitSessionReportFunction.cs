using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Reports
{
    public class SubmitSessionReportFunction
    {
        private readonly ILogger<SubmitSessionReportFunction> _logger;
        private readonly SessionReportService _sessionReportService;
        private readonly TableStorageService _storageService;
        private readonly TenantAdminsService _tenantAdminsService;
        private readonly GalacticAdminService _galacticAdminService;
        private readonly TelegramNotificationService _telegramNotificationService;

        public SubmitSessionReportFunction(
            ILogger<SubmitSessionReportFunction> logger,
            SessionReportService sessionReportService,
            TableStorageService storageService,
            TenantAdminsService tenantAdminsService,
            GalacticAdminService galacticAdminService,
            TelegramNotificationService telegramNotificationService)
        {
            _logger = logger;
            _sessionReportService = sessionReportService;
            _storageService = storageService;
            _tenantAdminsService = tenantAdminsService;
            _galacticAdminService = galacticAdminService;
            _telegramNotificationService = telegramNotificationService;
        }

        [Function("SubmitSessionReport")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sessions/{sessionId}/report")] HttpRequestData req,
            string sessionId)
        {
            _logger.LogInformation("SubmitSessionReport processing request for session {SessionId}", sessionId);

            try
            {
                // Validate authentication
                if (!TenantHelper.IsAuthenticated(req))
                {
                    _logger.LogWarning("Unauthenticated session report attempt for session {SessionId}", sessionId);
                    var unauthorizedResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                    await unauthorizedResponse.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Authentication required. Please provide a valid JWT token."
                    });
                    return unauthorizedResponse;
                }

                string tenantId = TenantHelper.GetTenantId(req);
                string userIdentifier = TenantHelper.GetUserIdentifier(req);

                // Require Tenant Admin or Galactic Admin
                var isGalacticAdmin = await _galacticAdminService.IsGalacticAdminAsync(userIdentifier);
                var isTenantAdmin = await _tenantAdminsService.IsTenantAdminAsync(tenantId, userIdentifier);
                if (!isGalacticAdmin && !isTenantAdmin)
                {
                    _logger.LogWarning("Non-admin user {UserIdentifier} attempted to submit session report for {SessionId}", userIdentifier, sessionId);
                    var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
                    await forbiddenResponse.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Access denied. Tenant Admin or Galactic Admin role required."
                    });
                    return forbiddenResponse;
                }

                // Parse request body
                var request = await req.ReadFromJsonAsync<SubmitSessionReportRequest>();
                if (request == null)
                {
                    var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequestResponse.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Invalid request body."
                    });
                    return badRequestResponse;
                }

                // Ensure sessionId consistency
                request.SessionId = sessionId;
                if (string.IsNullOrEmpty(request.TenantId))
                {
                    request.TenantId = tenantId;
                }

                // Submit report
                var metadata = await _sessionReportService.SubmitReportAsync(request, userIdentifier);

                // Log audit entry — skip for Galactic Admins
                if (!isGalacticAdmin)
                {
                    await _storageService.LogAuditEntryAsync(
                        request.TenantId,
                        "CREATE",
                        "SessionReport",
                        metadata.ReportId,
                        userIdentifier,
                        new Dictionary<string, string>
                        {
                            { "Action", "SubmitSessionReport" },
                            { "SessionId", sessionId },
                            { "BlobName", metadata.BlobName },
                            { "HasComment", (!string.IsNullOrEmpty(request.Comment)).ToString() },
                            { "HasEmail", (!string.IsNullOrEmpty(request.Email)).ToString() },
                            { "HasScreenshot", (!string.IsNullOrEmpty(request.ScreenshotBase64)).ToString() },
                            { "HasAgentLog", (!string.IsNullOrEmpty(request.AgentLogBase64)).ToString() }
                        }
                    );
                }

                // Telegram notification — best effort
                _ = _telegramNotificationService.SendSessionReportAsync(
                    request.TenantId, userIdentifier, sessionId, metadata.ReportId, request.Comment ?? string.Empty);

                _logger.LogInformation("Session report submitted: ReportId={ReportId}, Session={SessionId}, By={User}",
                    metadata.ReportId, sessionId, userIdentifier);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new SubmitSessionReportResponse
                {
                    Success = true,
                    Message = "Session report submitted successfully.",
                    ReportId = metadata.ReportId
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error submitting session report for {SessionId}", sessionId);

                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "Internal server error"
                });
                return errorResponse;
            }
        }
    }
}
