using System.Net;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Config
{
    public class PlanManagementFunction
    {
        private readonly ILogger<PlanManagementFunction> _logger;
        private readonly IConfigRepository _configRepo;

        public PlanManagementFunction(
            ILogger<PlanManagementFunction> logger,
            IConfigRepository configRepo)
        {
            _logger = logger;
            _configRepo = configRepo;
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

        private class SetPlanTierRequest
        {
            public string PlanTier { get; set; } = string.Empty;
        }
    }
}
