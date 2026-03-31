using System.Linq;
using System.Net;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Config
{
    /// <summary>
    /// Returns agent configuration including collector toggles and active gather rules
    /// Called by the agent at startup and periodically to pick up config changes
    /// Uses device authentication (client certificate), not JWT
    /// </summary>
    public class GetAgentConfigFunction
    {
        private readonly ILogger<GetAgentConfigFunction> _logger;
        private readonly TenantConfigurationService _configService;
        private readonly AdminConfigurationService _adminConfigService;
        private readonly GatherRuleService _gatherRuleService;
        private readonly ImeLogPatternService _imeLogPatternService;
        private readonly RateLimitService _rateLimitService;
        private readonly AutopilotDeviceValidator _autopilotDeviceValidator;
        private readonly CorporateIdentifierValidator _corporateIdentifierValidator;
        private readonly BootstrapSessionService _bootstrapSessionService;

        public GetAgentConfigFunction(
            ILogger<GetAgentConfigFunction> logger,
            TenantConfigurationService configService,
            AdminConfigurationService adminConfigService,
            GatherRuleService gatherRuleService,
            ImeLogPatternService imeLogPatternService,
            RateLimitService rateLimitService,
            AutopilotDeviceValidator autopilotDeviceValidator,
            CorporateIdentifierValidator corporateIdentifierValidator,
            BootstrapSessionService bootstrapSessionService)
        {
            _logger = logger;
            _configService = configService;
            _adminConfigService = adminConfigService;
            _gatherRuleService = gatherRuleService;
            _imeLogPatternService = imeLogPatternService;
            _rateLimitService = rateLimitService;
            _autopilotDeviceValidator = autopilotDeviceValidator;
            _corporateIdentifierValidator = corporateIdentifierValidator;
            _bootstrapSessionService = bootstrapSessionService;
        }

        [Function("GetAgentConfig")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "agent/config")] HttpRequestData req)
        {
            try
            {
                // Get tenantId from query parameter
                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                var tenantId = query["tenantId"];

                if (string.IsNullOrEmpty(tenantId))
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new
                    {
                        error = "tenantId query parameter is required"
                    });
                    return badRequest;
                }

                // Validate request security (certificate, rate limit, hardware whitelist)
                var (validation, errorResponse) = await req.ValidateSecurityAsync(
                    tenantId,
                    _configService,
                    _rateLimitService,
                    _autopilotDeviceValidator,
                    _corporateIdentifierValidator,
                    _logger,
                    bootstrapSessionService: _bootstrapSessionService
                );

                if (errorResponse != null)
                {
                    return errorResponse;
                }

                return await ProcessGetConfigAsync(req, tenantId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting agent config");
                var errorResp = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResp.WriteAsJsonAsync(new
                {
                    error = "Internal server error"
                });
                return errorResp;
            }
        }

        /// <summary>
        /// Core config logic: fetch tenant + admin config, gather rules, IME patterns.
        /// Called by both the cert-auth Run() method and the bootstrap wrapper.
        /// </summary>
        internal async Task<HttpResponseData> ProcessGetConfigAsync(HttpRequestData req, string tenantId)
        {
            _logger.LogInformation($"GetAgentConfig: Fetching config for tenant {tenantId}");

            // Get tenant configuration
            var tenantConfig = await _configService.GetConfigurationAsync(tenantId);

            // Get global admin config for platform-wide policy settings
            var adminConfig = await _adminConfigService.GetConfigurationAsync();

            // Build collector configuration from tenant settings + global policy
            var collectors = new CollectorConfiguration
            {
                EnablePerformanceCollector = tenantConfig.EnablePerformanceCollector,
                PerformanceIntervalSeconds = tenantConfig.PerformanceCollectorIntervalSeconds,
                CollectorIdleTimeoutMinutes = adminConfig.CollectorIdleTimeoutMinutes,
                HelloWaitTimeoutSeconds = tenantConfig.HelloWaitTimeoutSeconds,
                AgentMaxLifetimeMinutes = tenantConfig.AgentMaxLifetimeMinutes ?? 360
            };

            // Get active gather rules for this tenant (user-defined ad-hoc only)
            var gatherRules = await _gatherRuleService.GetActiveRulesForTenantAsync(tenantId);

            // Get active IME log patterns for this tenant (from Table Storage)
            var imeLogPatterns = await _imeLogPatternService.GetActivePatternsForTenantAsync(tenantId);

            // Merge global + tenant-specific diagnostics log paths
            var globalDiagPaths = adminConfig.GetDiagnosticsGlobalLogPaths();
            var tenantDiagPaths = tenantConfig.GetDiagnosticsLogPaths();
            var diagLogPaths = globalDiagPaths.Concat(tenantDiagPaths).ToList();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new AgentConfigResponse
            {
                ConfigVersion = 21, // NTP time check + timezone auto-set
                UploadIntervalSeconds = 10,
                SelfDestructOnComplete = tenantConfig.SelfDestructOnComplete ?? true,
                KeepLogFile = tenantConfig.KeepLogFile ?? false,
                EnableGeoLocation = tenantConfig.EnableGeoLocation ?? true,
                EnableImeMatchLog = tenantConfig.EnableImeMatchLog ?? false,
                MaxAuthFailures = tenantConfig.MaxAuthFailures ?? 5,
                AuthFailureTimeoutMinutes = tenantConfig.AuthFailureTimeoutMinutes ?? 0,
                LogLevel = tenantConfig.LogLevel ?? "Info",
                RebootOnComplete = tenantConfig.RebootOnComplete ?? false,
                RebootDelaySeconds = tenantConfig.RebootDelaySeconds ?? 10,
                ShowEnrollmentSummary = tenantConfig.ShowEnrollmentSummary ?? false,
                EnrollmentSummaryTimeoutSeconds = tenantConfig.EnrollmentSummaryTimeoutSeconds ?? 60,
                EnrollmentSummaryBrandingImageUrl = tenantConfig.EnrollmentSummaryBrandingImageUrl,
                EnrollmentSummaryLaunchRetrySeconds = tenantConfig.EnrollmentSummaryLaunchRetrySeconds ?? 120,
                MaxBatchSize = tenantConfig.MaxBatchSize ?? 100,
                DiagnosticsUploadEnabled = !string.IsNullOrEmpty(tenantConfig.DiagnosticsBlobSasUrl),
                DiagnosticsUploadMode = tenantConfig.DiagnosticsUploadMode ?? "Off",
                DiagnosticsLogPaths = diagLogPaths,
                Collectors = collectors,
                Analyzers = new AnalyzerConfiguration
                {
                    EnableLocalAdminAnalyzer = tenantConfig.EnableLocalAdminAnalyzer ?? true,
                    LocalAdminAllowedAccounts = tenantConfig.GetLocalAdminAllowedAccounts(),
                    EnableSoftwareInventoryAnalyzer = tenantConfig.EnableSoftwareInventoryAnalyzer ?? false
                },
                LatestAgentSha256 = adminConfig.LatestAgentSha256,
                NtpServer = string.IsNullOrEmpty(tenantConfig.NtpServer) ? "time.windows.com" : tenantConfig.NtpServer,
                EnableTimezoneAutoSet = tenantConfig.EnableTimezoneAutoSet ?? false,
                SendTraceEvents = tenantConfig.SendTraceEvents,
                UnrestrictedMode = tenantConfig.UnrestrictedModeEnabled && tenantConfig.UnrestrictedMode,
                GatherRules = gatherRules,
                ImeLogPatterns = imeLogPatterns
            });

            return response;
        }
    }
}
