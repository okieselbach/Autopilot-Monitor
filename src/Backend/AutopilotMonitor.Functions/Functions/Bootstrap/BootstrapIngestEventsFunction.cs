using System;
using System.Net;
using AutopilotMonitor.Functions.Functions.Ingest;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Extensions.SignalRService;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Bootstrap
{
    /// <summary>
    /// POST /api/bootstrap/ingest — cert-free ingest for pre-enrollment agents.
    /// Requires X-Bootstrap-Token header. Delegates to IngestEventsFunction.ProcessIngestAsync.
    /// </summary>
    public class BootstrapIngestEventsFunction
    {
        private readonly ILogger<BootstrapIngestEventsFunction> _logger;
        private readonly IngestEventsFunction _inner;
        private readonly TenantConfigurationService _configService;
        private readonly RateLimitService _rateLimitService;
        private readonly AutopilotDeviceValidator _autopilotDeviceValidator;
        private readonly CorporateIdentifierValidator _corporateIdentifierValidator;
        private readonly BootstrapSessionService _bootstrapSessionService;

        public BootstrapIngestEventsFunction(
            ILogger<BootstrapIngestEventsFunction> logger,
            IngestEventsFunction inner,
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

        [Function("BootstrapIngestEvents")]
        public async Task<IngestEventsOutput> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "bootstrap/ingest")] HttpRequestData req)
        {
            try
            {
                // Bootstrap-only: reject requests without X-Bootstrap-Token
                if (!req.Headers.Contains("X-Bootstrap-Token"))
                {
                    var noToken = req.CreateResponse(HttpStatusCode.Unauthorized);
                    await noToken.WriteAsJsonAsync(new { success = false, message = "X-Bootstrap-Token header is required" });
                    return new IngestEventsOutput { HttpResponse = noToken, SignalRMessages = Array.Empty<SignalRMessageAction>() };
                }

                var tenantId = req.Headers.Contains("X-Tenant-Id")
                    ? req.Headers.GetValues("X-Tenant-Id").FirstOrDefault()
                    : null;

                if (string.IsNullOrEmpty(tenantId))
                {
                    var badReq = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badReq.WriteAsJsonAsync(new { success = false, message = "X-Tenant-Id header is required" });
                    return new IngestEventsOutput { HttpResponse = badReq, SignalRMessages = Array.Empty<SignalRMessageAction>() };
                }

                // Feature gate: bootstrap endpoints only available when explicitly enabled for this tenant
                var (config, tenantExists) = await _configService.TryGetConfigurationAsync(tenantId);
                if (!tenantExists || !config.BootstrapTokenEnabled)
                {
                    return new IngestEventsOutput { HttpResponse = req.CreateResponse(HttpStatusCode.NotFound), SignalRMessages = Array.Empty<SignalRMessageAction>() };
                }

                var (validation, errorResponse) = await req.ValidateSecurityAsync(
                    tenantId, _configService, _rateLimitService,
                    _autopilotDeviceValidator, _corporateIdentifierValidator,
                    _logger, bootstrapSessionService: _bootstrapSessionService);

                if (errorResponse != null)
                {
                    return new IngestEventsOutput { HttpResponse = errorResponse, SignalRMessages = Array.Empty<SignalRMessageAction>() };
                }

                return await _inner.ProcessIngestAsync(req, tenantId, validation);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in bootstrap ingest");
                var error = req.CreateResponse(HttpStatusCode.InternalServerError);
                await error.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return new IngestEventsOutput { HttpResponse = error, SignalRMessages = Array.Empty<SignalRMessageAction>() };
            }
        }
    }
}
