using System.Net;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Functions.Services.Vulnerability;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Extensions.SignalRService;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Ingest
{
    public partial class IngestEventsFunction
    {
        private readonly ILogger<IngestEventsFunction> _logger;
        private readonly ISessionRepository _sessionRepo;
        private readonly IMetricsRepository _metricsRepo;
        private readonly IMaintenanceRepository _maintenanceRepo;
        private readonly IRuleRepository _ruleRepo;
        private readonly IVulnerabilityRepository _vulnRepo;
        private readonly TenantConfigurationService _configService;
        private readonly RateLimitService _rateLimitService;
        private readonly AutopilotDeviceValidator _autopilotDeviceValidator;
        private readonly CorporateIdentifierValidator _corporateIdentifierValidator;
        private readonly AnalyzeRuleService _analyzeRuleService;
        private readonly WebhookNotificationService _webhookNotificationService;
        private readonly BlockedDeviceService _blockedDeviceService;
        private readonly BlockedVersionService _blockedVersionService;
        private readonly BootstrapSessionService _bootstrapSessionService;
        private readonly VulnerabilityCorrelationService _vulnerabilityCorrelation;
        private readonly AdminConfigurationService _adminConfigService;
        private readonly SignalRNotificationService _signalRNotification;

        public IngestEventsFunction(
            ILogger<IngestEventsFunction> logger,
            ISessionRepository sessionRepo,
            IMetricsRepository metricsRepo,
            IMaintenanceRepository maintenanceRepo,
            IRuleRepository ruleRepo,
            IVulnerabilityRepository vulnRepo,
            TenantConfigurationService configService,
            RateLimitService rateLimitService,
            AutopilotDeviceValidator autopilotDeviceValidator,
            CorporateIdentifierValidator corporateIdentifierValidator,
            AnalyzeRuleService analyzeRuleService,
            WebhookNotificationService webhookNotificationService,
            BlockedDeviceService blockedDeviceService,
            BlockedVersionService blockedVersionService,
            BootstrapSessionService bootstrapSessionService,
            VulnerabilityCorrelationService vulnerabilityCorrelation,
            AdminConfigurationService adminConfigService,
            SignalRNotificationService signalRNotification)
        {
            _logger = logger;
            _sessionRepo = sessionRepo;
            _metricsRepo = metricsRepo;
            _maintenanceRepo = maintenanceRepo;
            _ruleRepo = ruleRepo;
            _vulnRepo = vulnRepo;
            _configService = configService;
            _rateLimitService = rateLimitService;
            _autopilotDeviceValidator = autopilotDeviceValidator;
            _corporateIdentifierValidator = corporateIdentifierValidator;
            _analyzeRuleService = analyzeRuleService;
            _webhookNotificationService = webhookNotificationService;
            _blockedDeviceService = blockedDeviceService;
            _blockedVersionService = blockedVersionService;
            _bootstrapSessionService = bootstrapSessionService;
            _vulnerabilityCorrelation = vulnerabilityCorrelation;
            _adminConfigService = adminConfigService;
            _signalRNotification = signalRNotification;
        }

        [Function("IngestEvents")]
        public async Task<IngestEventsOutput> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "agent/ingest")] HttpRequestData req)
        {
            try
            {
                // --- Security checks FIRST — before touching the request body ---

                // TenantId is available in the X-Tenant-Id header, so we can validate the request
                // before paying the cost of gzip decompression and JSON parsing.
                var tenantIdHeader = req.Headers.Contains("X-Tenant-Id")
                    ? req.Headers.GetValues("X-Tenant-Id").FirstOrDefault()
                    : null;

                if (string.IsNullOrEmpty(tenantIdHeader))
                {
                    return await CreateErrorResponse(req, HttpStatusCode.BadRequest, "X-Tenant-Id header is required");
                }

                // Validate request security (certificate, rate limit, hardware whitelist, serial number in autopilot)
                // sessionId is not yet known at this point — that's fine, it's optional for logging only.
                var (validation, errorResponse) = await req.ValidateSecurityAsync(
                    tenantIdHeader,
                    _configService,
                    _rateLimitService,
                    _autopilotDeviceValidator,
                    _corporateIdentifierValidator,
                    _logger,
                    bootstrapSessionService: _bootstrapSessionService
                );

                if (errorResponse != null)
                {
                    // Security validation failed - return before parsing the body
                    return new IngestEventsOutput
                    {
                        HttpResponse = errorResponse,
                        SignalRMessages = Array.Empty<SignalRMessageAction>()
                    };
                }

                return await ProcessIngestAsync(req, tenantIdHeader, validation);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error ingesting events");
                return await CreateErrorResponse(req, HttpStatusCode.InternalServerError, "Internal server error");
            }
        }
    }

    public class IngestEventsOutput
    {
        [HttpResult]
        public HttpResponseData? HttpResponse { get; set; }

        [SignalROutput(HubName = "autopilotmonitor")]
        public SignalRMessageAction[]? SignalRMessages { get; set; }
    }

    /// <summary>
    /// NDJSON metadata (first line of NDJSON payload)
    /// </summary>
    internal class NdjsonMetadata
    {
        public string SessionId { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
    }

    internal class AppInstallAggregationState
    {
        public AppInstallSummary Summary { get; set; } = new();
        public DateTime? DownloadStartedAt { get; set; }
        public DateTime? InstallStartedAt { get; set; }
    }
}
