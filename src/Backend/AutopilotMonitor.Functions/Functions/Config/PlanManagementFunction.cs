using System.Net;
using System.Text.Json;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models.Config;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Config
{
    public class PlanManagementFunction
    {
        private readonly ILogger<PlanManagementFunction> _logger;
        private readonly IConfigRepository _configRepo;
        private readonly AdminConfigurationService _adminConfigService;

        public PlanManagementFunction(
            ILogger<PlanManagementFunction> logger,
            IConfigRepository configRepo,
            AdminConfigurationService adminConfigService)
        {
            _logger = logger;
            _configRepo = configRepo;
            _adminConfigService = adminConfigService;
        }

        /// <summary>
        /// PATCH /api/config/{tenantId}/plan
        /// Sets the plan tier for a tenant. Body: { "planTier": "pro" }
        /// </summary>
        [Function("SetTenantPlanTier")]
        public async Task<HttpResponseData> SetPlanTier(
            [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "config/{tenantId}/plan")] HttpRequestData req,
            string tenantId)
        {
            _logger.LogInformation("SetTenantPlanTier: tenantId={TenantId}", tenantId);

            try
            {
                var body = await req.ReadFromJsonAsync<SetPlanTierRequest>();
                if (body == null || string.IsNullOrWhiteSpace(body.PlanTier))
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { error = "planTier is required" });
                    return badRequest;
                }

                var validTiers = new[] { "free", "pro", "enterprise" };
                if (!validTiers.Contains(body.PlanTier.ToLowerInvariant()))
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { error = $"Invalid planTier. Valid values: {string.Join(", ", validTiers)}" });
                    return badRequest;
                }

                var config = await _configRepo.GetTenantConfigurationAsync(tenantId);
                if (config == null)
                {
                    var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFound.WriteAsJsonAsync(new { error = "Tenant not found" });
                    return notFound;
                }

                config.PlanTier = body.PlanTier.ToLowerInvariant();
                await _configRepo.SaveTenantConfigurationAsync(config);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new { tenantId, planTier = config.PlanTier });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting plan tier");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { error = "Internal server error" });
                return errorResponse;
            }
        }

        /// <summary>
        /// GET /api/global/config/plan-tiers
        /// Returns plan tier definitions from AdminConfiguration.PlanTierDefinitionsJson.
        /// </summary>
        [Function("GetPlanTierDefinitions")]
        public async Task<HttpResponseData> GetPlanTierDefinitions(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/config/plan-tiers")] HttpRequestData req)
        {
            try
            {
                var config = await _adminConfigService.GetConfigurationAsync();
                var tiers = ParsePlanTierDefinitions(config.PlanTierDefinitionsJson);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new { tiers });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting plan tier definitions");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { error = "Internal server error" });
                return errorResponse;
            }
        }

        /// <summary>
        /// PUT /api/global/config/plan-tiers
        /// Saves plan tier definitions. Body: { "tiers": [...] }
        /// </summary>
        [Function("SetPlanTierDefinitions")]
        public async Task<HttpResponseData> SetPlanTierDefinitions(
            [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "global/config/plan-tiers")] HttpRequestData req)
        {
            try
            {
                var body = await req.ReadFromJsonAsync<SetPlanTierDefinitionsRequest>();
                if (body?.Tiers == null || body.Tiers.Count == 0)
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { error = "At least one tier definition is required" });
                    return badRequest;
                }

                // Validate tier names are unique
                var names = body.Tiers.Select(t => t.Name.ToLowerInvariant()).ToList();
                if (names.Distinct().Count() != names.Count)
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { error = "Tier names must be unique" });
                    return badRequest;
                }

                // Normalize names to lowercase
                foreach (var tier in body.Tiers)
                    tier.Name = tier.Name.ToLowerInvariant();

                var config = await _adminConfigService.GetConfigurationAsync();
                config.PlanTierDefinitionsJson = JsonSerializer.Serialize(body.Tiers);
                await _adminConfigService.SaveConfigurationAsync(config);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new { tiers = body.Tiers });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving plan tier definitions");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { error = "Internal server error" });
                return errorResponse;
            }
        }

        private static List<PlanTierDefinition> ParsePlanTierDefinitions(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new List<PlanTierDefinition>();

            try
            {
                return JsonSerializer.Deserialize<List<PlanTierDefinition>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<PlanTierDefinition>();
            }
            catch
            {
                return new List<PlanTierDefinition>();
            }
        }

        private class SetPlanTierRequest
        {
            public string PlanTier { get; set; } = string.Empty;
        }

        private class SetPlanTierDefinitionsRequest
        {
            public List<PlanTierDefinition> Tiers { get; set; } = new();
        }
    }
}
