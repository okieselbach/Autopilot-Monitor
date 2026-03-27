using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Extensions;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Feedback
{
    public class FeedbackFunction
    {
        private readonly ILogger<FeedbackFunction> _logger;
        private readonly TenantConfigurationService _tenantConfigService;
        private readonly AdminConfigurationService _adminConfigService;
        private readonly TenantAdminsService _tenantAdminsService;
        private readonly TelegramNotificationService _telegramNotificationService;
        private readonly ISessionRepository _sessionRepo;
        private readonly IConfigRepository _configRepo;

        public FeedbackFunction(
            ILogger<FeedbackFunction> logger,
            TenantConfigurationService tenantConfigService,
            AdminConfigurationService adminConfigService,
            TenantAdminsService tenantAdminsService,
            TelegramNotificationService telegramNotificationService,
            ISessionRepository sessionRepo,
            IConfigRepository configRepo)
        {
            _logger = logger;
            _tenantConfigService = tenantConfigService;
            _adminConfigService = adminConfigService;
            _tenantAdminsService = tenantAdminsService;
            _telegramNotificationService = telegramNotificationService;
            _sessionRepo = sessionRepo;
            _configRepo = configRepo;
        }

        /// <summary>
        /// Checks whether the current user is eligible to see the feedback bubble.
        /// Evaluates: kill-switch, role, tenant age, session existence, cooldown.
        /// </summary>
        [Function("GetFeedbackStatus")]
        public async Task<HttpResponseData> GetStatus(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "feedback/status")] HttpRequestData req)
        {
            try
            {
                // 1. Kill-switch
                var adminConfig = await _adminConfigService.GetConfigurationAsync();
                if (!adminConfig.FeedbackEnabled)
                    return await WriteJson(req, new { eligible = false });

                // 2. User identity
                string tenantId = TenantHelper.GetTenantId(req);
                string upn = TenantHelper.GetUserIdentifier(req);

                // 3. Role check — only Admin + Operator
                var roleInfo = await _tenantAdminsService.GetMemberRoleAsync(tenantId, upn);
                if (roleInfo == null || roleInfo.Role == Constants.TenantRoles.Viewer)
                    return await WriteJson(req, new { eligible = false });

                // 4. Tenant age check
                var tenantConfig = await _tenantConfigService.GetConfigurationAsync(tenantId);
                if (tenantConfig.OnboardedAt == null ||
                    (DateTime.UtcNow - tenantConfig.OnboardedAt.Value).TotalDays < adminConfig.FeedbackMinTenantAgeDays)
                    return await WriteJson(req, new { eligible = false });

                // 5. Sessions check — at least 1 session exists
                var sessionPage = await _sessionRepo.GetSessionsAsync(tenantId, maxResults: 1);
                if (sessionPage.Sessions.Count == 0)
                    return await WriteJson(req, new { eligible = false });

                // 6. Cooldown check
                var feedbackEntry = await _configRepo.GetFeedbackEntryAsync(upn);
                if (feedbackEntry?.InteractedAt != null)
                {
                    // Cooldown = 0 means single wave only
                    if (adminConfig.FeedbackCooldownDays == 0)
                        return await WriteJson(req, new { eligible = false });

                    var daysSinceInteraction = (DateTime.UtcNow - feedbackEntry.InteractedAt.Value).TotalDays;
                    if (daysSinceInteraction < adminConfig.FeedbackCooldownDays)
                        return await WriteJson(req, new { eligible = false });
                }

                return await WriteJson(req, new { eligible = true });
            }
            catch (UnauthorizedAccessException)
            {
                var response = req.CreateResponse(HttpStatusCode.Unauthorized);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking feedback status");
                // Fail closed — don't show bubble on errors
                return await WriteJson(req, new { eligible = false });
            }
        }

        /// <summary>
        /// Submits user feedback or records a dismissal.
        /// On submit: stores feedback and sends Telegram notification.
        /// On dismiss: stores dismissal so the bubble is not shown again (until cooldown).
        /// </summary>
        [Function("SubmitFeedback")]
        public async Task<HttpResponseData> Submit(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "feedback")] HttpRequestData req)
        {
            try
            {
                string tenantId = TenantHelper.GetTenantId(req);
                string upn = TenantHelper.GetUserIdentifier(req);
                var principal = req.FunctionContext.GetUser();
                string displayName = principal?.GetDisplayName() ?? upn;

                // Request body size limit (1 MB)
                if (req.Headers.TryGetValues("Content-Length", out var clValues)
                    && long.TryParse(clValues.FirstOrDefault(), out var contentLength)
                    && contentLength > 1_048_576)
                {
                    var tooLarge = req.CreateResponse(HttpStatusCode.BadRequest);
                    await tooLarge.WriteAsJsonAsync(new { success = false, message = "Request body too large" });
                    return tooLarge;
                }

                var body = await req.ReadFromJsonAsync<FeedbackRequest>();
                if (body == null)
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { success = false, message = "Invalid request body" });
                    return badRequest;
                }

                // Validate rating
                if (!body.Dismissed && (body.Rating < 1 || body.Rating > 5))
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { success = false, message = "Rating must be between 1 and 5" });
                    return badRequest;
                }

                // Trim comment
                var comment = body.Comment?.Trim();
                if (comment?.Length > 500)
                    comment = comment.Substring(0, 500);

                // Upsert feedback record
                await _configRepo.SaveFeedbackEntryAsync(new FeedbackEntry
                {
                    Upn = upn,
                    TenantId = tenantId,
                    DisplayName = displayName,
                    Rating = body.Dismissed ? null : (int?)body.Rating,
                    Comment = body.Dismissed ? null : comment,
                    Dismissed = body.Dismissed,
                    Submitted = !body.Dismissed,
                    InteractedAt = DateTime.UtcNow
                });

                // Telegram notification — only for actual submissions, fire-and-forget
                if (!body.Dismissed && body.Rating > 0)
                {
                    _ = _telegramNotificationService.SendFeedbackAsync(tenantId, upn, displayName, body.Rating, comment);
                }

                _logger.LogInformation("Feedback {Action} by {Upn} (tenant {TenantId})",
                    body.Dismissed ? "dismissed" : "submitted", upn, tenantId);

                return await WriteJson(req, new { success = true });
            }
            catch (UnauthorizedAccessException)
            {
                return req.CreateResponse(HttpStatusCode.Unauthorized);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error submitting feedback");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return errorResponse;
            }
        }

        /// <summary>
        /// Returns all feedback entries for the Global Admin dashboard.
        /// </summary>
        [Function("GetAllFeedback")]
        public async Task<HttpResponseData> GetAll(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "feedback/all")] HttpRequestData req)
        {
            try
            {
                var allEntries = await _configRepo.GetAllFeedbackEntriesAsync();
                var entries = allEntries.Select(e => new
                {
                    upn = e.Upn,
                    tenantId = e.TenantId,
                    displayName = e.DisplayName,
                    rating = e.Rating,
                    comment = e.Comment,
                    dismissed = e.Dismissed,
                    submitted = e.Submitted,
                    interactedAt = e.InteractedAt?.ToString("o")
                }).ToList();

                return await WriteJson(req, new { feedback = entries });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching all feedback");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return errorResponse;
            }
        }

        private static async Task<HttpResponseData> WriteJson(HttpRequestData req, object data)
        {
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(data);
            return response;
        }
    }

    public class FeedbackRequest
    {
        public int Rating { get; set; }
        public string? Comment { get; set; }
        public bool Dismissed { get; set; }
    }
}
