using System;
using System.Net;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions
{
    /// <summary>
    /// POST /api/bootstrap/error — cert-free error reporting for pre-enrollment agents.
    /// Requires X-Bootstrap-Token header. Delegates to ReportAgentErrorFunction.ProcessReportErrorAsync.
    /// </summary>
    public class BootstrapReportAgentErrorFunction
    {
        private readonly ILogger<BootstrapReportAgentErrorFunction> _logger;
        private readonly ReportAgentErrorFunction _inner;
        private readonly TenantConfigurationService _configService;
        private readonly RateLimitService _rateLimitService;
        private readonly AutopilotDeviceValidator _autopilotDeviceValidator;
        private readonly CorporateIdentifierValidator _corporateIdentifierValidator;
        private readonly BootstrapSessionService _bootstrapSessionService;

        public BootstrapReportAgentErrorFunction(
            ILogger<BootstrapReportAgentErrorFunction> logger,
            ReportAgentErrorFunction inner,
            TenantConfigurationService configService,
            RateLimitService rateLimitService,
            AutopilotDeviceValidator autopilotDeviceValidator,
            CorporateIdentifierValidator corporateIdentifierValidator,
            BootstrapSessionService bootstrapSessionService)
        {
            _logger = logger;
            _inner = inner;
            _configService = configService;
            _rateLimitService = rateLimitService;
            _autopilotDeviceValidator = autopilotDeviceValidator;
            _corporateIdentifierValidator = corporateIdentifierValidator;
            _bootstrapSessionService = bootstrapSessionService;
        }

        [Function("BootstrapReportAgentError")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "bootstrap/error")] HttpRequestData req)
        {
            try
            {
                // Bootstrap-only: reject requests without X-Bootstrap-Token
                if (!req.Headers.Contains("X-Bootstrap-Token"))
                {
                    var noToken = req.CreateResponse(HttpStatusCode.Unauthorized);
                    await noToken.WriteAsJsonAsync(new { error = "X-Bootstrap-Token header is required" });
                    return noToken;
                }

                var tenantId = req.Headers.Contains("X-Tenant-Id")
                    ? req.Headers.GetValues("X-Tenant-Id").FirstOrDefault()
                    : null;

                if (string.IsNullOrEmpty(tenantId))
                {
                    return req.CreateResponse(HttpStatusCode.BadRequest);
                }

                // Feature gate: bootstrap endpoints only available when explicitly enabled for this tenant
                var (config, tenantExists) = await _configService.TryGetConfigurationAsync(tenantId);
                if (!tenantExists || !config.BootstrapTokenEnabled)
                {
                    return req.CreateResponse(HttpStatusCode.NotFound);
                }

                var (_, errorResponse) = await req.ValidateSecurityAsync(
                    tenantId, _configService, _rateLimitService,
                    _autopilotDeviceValidator, _corporateIdentifierValidator,
                    _logger, bootstrapSessionService: _bootstrapSessionService);

                if (errorResponse != null)
                {
                    return errorResponse;
                }

                return await _inner.ProcessReportErrorAsync(req, tenantId);
            }
            catch (Exception ex)
            {
                // Return 200 even on unexpected errors — same as original
                _logger.LogError(ex, "BootstrapReportAgentError: Unexpected error");
                return req.CreateResponse(HttpStatusCode.OK);
            }
        }
    }
}
