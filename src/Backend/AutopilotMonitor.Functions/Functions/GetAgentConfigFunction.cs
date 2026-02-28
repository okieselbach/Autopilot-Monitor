using System.Linq;
using System.Net;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions
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

        public GetAgentConfigFunction(
            ILogger<GetAgentConfigFunction> logger,
            TenantConfigurationService configService,
            AdminConfigurationService adminConfigService,
            GatherRuleService gatherRuleService,
            ImeLogPatternService imeLogPatternService,
            RateLimitService rateLimitService,
            AutopilotDeviceValidator autopilotDeviceValidator)
        {
            _logger = logger;
            _configService = configService;
            _adminConfigService = adminConfigService;
            _gatherRuleService = gatherRuleService;
            _imeLogPatternService = imeLogPatternService;
            _rateLimitService = rateLimitService;
            _autopilotDeviceValidator = autopilotDeviceValidator;
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
                    _logger
                );

                if (errorResponse != null)
                {
                    return errorResponse;
                }

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
                    MaxCollectorDurationHours = adminConfig.MaxCollectorDurationHours
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
                    ConfigVersion = 9, // diagnostics log paths now configurable via portal
                    UploadIntervalSeconds = 30,
                    SelfDestructOnComplete = tenantConfig.SelfDestructOnComplete ?? true,
                    KeepLogFile = tenantConfig.KeepLogFile ?? false,
                    EnableGeoLocation = tenantConfig.EnableGeoLocation ?? true,
                    EnableImeMatchLog = tenantConfig.EnableImeMatchLog ?? false,
                    MaxAuthFailures = tenantConfig.MaxAuthFailures ?? 5,
                    AuthFailureTimeoutMinutes = tenantConfig.AuthFailureTimeoutMinutes ?? 0,
                    LogLevel = tenantConfig.LogLevel ?? "Info",
                    RebootOnComplete = tenantConfig.RebootOnComplete ?? false,
                    RebootDelaySeconds = tenantConfig.RebootDelaySeconds ?? 10,
                    MaxBatchSize = tenantConfig.MaxBatchSize ?? 100,
                    DiagnosticsUploadEnabled = !string.IsNullOrEmpty(tenantConfig.DiagnosticsBlobSasUrl),
                    DiagnosticsUploadMode = tenantConfig.DiagnosticsUploadMode ?? "Off",
                    DiagnosticsLogPaths = diagLogPaths,
                    Collectors = collectors,
                    GatherRules = gatherRules,
                    ImeLogPatterns = imeLogPatterns
                });

                return response;
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
    }
}
