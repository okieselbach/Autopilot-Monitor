using System.Net;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Extensions.SignalRService;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Functions
{
    public class RegisterSessionFunction
    {
        private readonly ILogger<RegisterSessionFunction> _logger;
        private readonly TableStorageService _storageService;
        private readonly TenantConfigurationService _configService;
        private readonly RateLimitService _rateLimitService;

        public RegisterSessionFunction(
            ILogger<RegisterSessionFunction> logger,
            TableStorageService storageService,
            TenantConfigurationService configService,
            RateLimitService rateLimitService)
        {
            _logger = logger;
            _storageService = storageService;
            _configService = configService;
            _rateLimitService = rateLimitService;
        }

        [Function("RegisterSession")]
        public async Task<RegisterSessionOutput> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sessions/register")] HttpRequestData req)
        {
            _logger.LogInformation("RegisterSession function processing request");

            try
            {
                // Parse request
                if (req.Body.Length > 1_048_576) // 1 MB limit
                {
                    var errorResponse = await CreateErrorResponse(req, HttpStatusCode.BadRequest, "Request body too large");
                    return new RegisterSessionOutput { HttpResponse = errorResponse };
                }
                var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                var request = JsonConvert.DeserializeObject<RegisterSessionRequest>(requestBody);

                if (request?.Registration == null)
                {
                    var errorResponse = await CreateErrorResponse(req, HttpStatusCode.BadRequest, "Invalid request payload");
                    return new RegisterSessionOutput { HttpResponse = errorResponse };
                }

                var registration = request.Registration;

                // Validate request security (certificate, rate limit, hardware whitelist)
                var (validation, errorResponse2) = await req.ValidateSecurityAsync(
                    registration.TenantId,
                    _configService,
                    _rateLimitService,
                    _logger
                );

                if (errorResponse2 != null)
                {
                    return new RegisterSessionOutput { HttpResponse = errorResponse2 };
                }

                _logger.LogInformation($"Registering session {registration.SessionId} for tenant {registration.TenantId} (Device: {validation.CertificateThumbprint})");

                // Store session in Azure Table Storage
                var stored = await _storageService.StoreSessionAsync(registration);

                if (!stored)
                {
                    var errorResponse = await CreateErrorResponse(req, HttpStatusCode.InternalServerError, "Failed to store session");
                    return new RegisterSessionOutput { HttpResponse = errorResponse };
                }

                // Increment platform stats (fire-and-forget, non-blocking)
                _ = _storageService.IncrementPlatformStatAsync("TotalEnrollments");

                // Retrieve the stored session to include full data in SignalR message
                var session = await _storageService.GetSessionAsync(registration.TenantId, registration.SessionId);

                var response = req.CreateResponse(HttpStatusCode.OK);
                var responseData = new RegisterSessionResponse
                {
                    SessionId = registration.SessionId,
                    Success = true,
                    Message = "Session registered successfully",
                    RegisteredAt = DateTime.UtcNow
                };

                await response.WriteAsJsonAsync(responseData);

                // Send SignalR notification for new session registration
                // This is sent to BOTH tenant-specific group AND galactic-admins group
                // so Galactic Admins can see new sessions from all tenants without being
                // flooded with every single event update
                var messagePayload = new {
                    sessionId = registration.SessionId,
                    tenantId = registration.TenantId,
                    session = session
                };

                // 1. Tenant-specific notification (normal users)
                var tenantMessage = new SignalRMessageAction("newSession")
                {
                    GroupName = $"tenant-{registration.TenantId}",
                    Arguments = new[] { messagePayload }
                };

                // 2. Galactic Admins notification (cross-tenant visibility)
                var galacticAdminMessage = new SignalRMessageAction("newSession")
                {
                    GroupName = "galactic-admins",
                    Arguments = new[] { messagePayload }
                };

                return new RegisterSessionOutput
                {
                    HttpResponse = response,
                    SignalRMessages = new[] { tenantMessage, galacticAdminMessage }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error registering session");
                var errorResponse = await CreateErrorResponse(req, HttpStatusCode.InternalServerError, "Internal server error");
                return new RegisterSessionOutput { HttpResponse = errorResponse };
            }
        }

        private async Task<HttpResponseData> CreateErrorResponse(HttpRequestData req, HttpStatusCode statusCode, string message)
        {
            var response = req.CreateResponse(statusCode);
            var errorResponse = new RegisterSessionResponse
            {
                Success = false,
                Message = message,
                RegisteredAt = DateTime.UtcNow
            };
            await response.WriteAsJsonAsync(errorResponse);
            return response;
        }
    }

    public class RegisterSessionOutput
    {
        [HttpResult]
        public HttpResponseData? HttpResponse { get; set; }

        [SignalROutput(HubName = "autopilotmonitor")]
        public SignalRMessageAction[]? SignalRMessages { get; set; }
    }
}
