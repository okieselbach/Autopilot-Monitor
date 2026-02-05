using System.Net;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
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
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sessions/register")] HttpRequestData req)
        {
            _logger.LogInformation("RegisterSession function processing request");

            try
            {
                // Parse request
                var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                var request = JsonConvert.DeserializeObject<RegisterSessionRequest>(requestBody);

                if (request?.Registration == null)
                {
                    return await CreateErrorResponse(req, HttpStatusCode.BadRequest, "Invalid request payload");
                }

                var registration = request.Registration;

                // Validate request security (certificate, rate limit, hardware whitelist)
                var (validation, errorResponse) = await req.ValidateSecurityAsync(
                    registration.TenantId,
                    _configService,
                    _rateLimitService,
                    _logger
                );

                if (errorResponse != null)
                {
                    return errorResponse;
                }

                _logger.LogInformation($"Registering session {registration.SessionId} for tenant {registration.TenantId} (Device: {validation.CertificateThumbprint})");

                // Store session in Azure Table Storage
                var stored = await _storageService.StoreSessionAsync(registration);

                if (!stored)
                {
                    return await CreateErrorResponse(req, HttpStatusCode.InternalServerError, "Failed to store session");
                }

                var response = req.CreateResponse(HttpStatusCode.OK);
                var responseData = new RegisterSessionResponse
                {
                    SessionId = registration.SessionId,
                    Success = true,
                    Message = "Session registered successfully",
                    RegisteredAt = DateTime.UtcNow
                };

                await response.WriteAsJsonAsync(responseData);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error registering session");
                return await CreateErrorResponse(req, HttpStatusCode.InternalServerError, "Internal server error");
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
}
