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
        private readonly GatherRuleService _gatherRuleService;
        private readonly RateLimitService _rateLimitService;
        private readonly SerialNumberValidator _serialNumberValidator;

        public GetAgentConfigFunction(
            ILogger<GetAgentConfigFunction> logger,
            TenantConfigurationService configService,
            GatherRuleService gatherRuleService,
            RateLimitService rateLimitService,
            SerialNumberValidator serialNumberValidator)
        {
            _logger = logger;
            _configService = configService;
            _gatherRuleService = gatherRuleService;
            _rateLimitService = rateLimitService;
            _serialNumberValidator = serialNumberValidator;
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
                    _serialNumberValidator,
                    _logger
                );

                if (errorResponse != null)
                {
                    return errorResponse;
                }

                _logger.LogInformation($"GetAgentConfig: Fetching config for tenant {tenantId}");

                // Get tenant configuration
                var tenantConfig = await _configService.GetConfigurationAsync(tenantId);

                // Build collector configuration from tenant settings
                var collectors = new CollectorConfiguration
                {
                    EnablePerformanceCollector = tenantConfig.EnablePerformanceCollector,
                    PerformanceIntervalSeconds = tenantConfig.PerformanceCollectorIntervalSeconds
                };

                // Get active gather rules for this tenant (user-defined ad-hoc only)
                var gatherRules = await _gatherRuleService.GetActiveRulesForTenantAsync(tenantId);

                // Get built-in IME log patterns for smart enrollment tracking
                var imeLogPatterns = BuiltInImeLogPatterns.GetAll();

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new AgentConfigResponse
                {
                    ConfigVersion = 4,
                    UploadIntervalSeconds = 30,
                    CleanupOnExit = false,
                    SelfDestructOnComplete = false,
                    KeepLogFile = true,
                    Collectors = collectors,
                    GatherRules = gatherRules,
                    ImeLogPatterns = imeLogPatterns,
                    MaxAuthFailures = tenantConfig.MaxAuthFailures ?? 5,
                    AuthFailureTimeoutMinutes = tenantConfig.AuthFailureTimeoutMinutes ?? 0,
                    LogLevel = tenantConfig.LogLevel ?? "Info",
                    RebootOnComplete = tenantConfig.RebootOnComplete ?? false,
                    MaxBatchSize = tenantConfig.MaxBatchSize ?? 100
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
