using System.Net;
using System.Web;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions
{
    public class GetSessionFunction
    {
        private readonly ILogger<GetSessionFunction> _logger;
        private readonly TableStorageService _storageService;
        private readonly GalacticAdminService _galacticAdminService;

        public GetSessionFunction(
            ILogger<GetSessionFunction> logger,
            TableStorageService storageService,
            GalacticAdminService galacticAdminService)
        {
            _logger = logger;
            _storageService = storageService;
            _galacticAdminService = galacticAdminService;
        }

        [Function("GetSession")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sessions/{sessionId}")] HttpRequestData req,
            string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "SessionId is required"
                });
                return badRequest;
            }

            var sessionPrefix = $"[Session: {sessionId.Substring(0, Math.Min(8, sessionId.Length))}]";

            try
            {
                if (!TenantHelper.IsAuthenticated(req))
                {
                    _logger.LogWarning($"{sessionPrefix} Unauthenticated GetSession attempt");
                    var unauthorizedResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                    await unauthorizedResponse.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Authentication required. Please provide a valid JWT token."
                    });
                    return unauthorizedResponse;
                }

                var userTenantId = TenantHelper.GetTenantId(req);
                var userIdentifier = TenantHelper.GetUserIdentifier(req);
                var query = HttpUtility.ParseQueryString(req.Url.Query);
                var requestedTenantId = query["tenantId"];
                var effectiveTenantId = string.IsNullOrWhiteSpace(requestedTenantId) ? userTenantId : requestedTenantId;

                if (string.IsNullOrWhiteSpace(effectiveTenantId))
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Unable to resolve tenantId."
                    });
                    return badRequest;
                }

                if (!string.Equals(effectiveTenantId, userTenantId, StringComparison.OrdinalIgnoreCase))
                {
                    var isGalacticAdmin = await _galacticAdminService.IsGalacticAdminAsync(userIdentifier);
                    if (!isGalacticAdmin)
                    {
                        _logger.LogWarning($"{sessionPrefix} User {userIdentifier} (tenant {userTenantId}) attempted to access session in tenant {effectiveTenantId}");
                        var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
                        await forbiddenResponse.WriteAsJsonAsync(new
                        {
                            success = false,
                            message = "Access denied. You can only view sessions for your own tenant."
                        });
                        return forbiddenResponse;
                    }
                }

                var session = await _storageService.GetSessionAsync(effectiveTenantId, sessionId);
                if (session == null)
                {
                    var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFound.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Session not found",
                        sessionId
                    });
                    return notFound;
                }

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    session
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"{sessionPrefix} Error getting session");
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
