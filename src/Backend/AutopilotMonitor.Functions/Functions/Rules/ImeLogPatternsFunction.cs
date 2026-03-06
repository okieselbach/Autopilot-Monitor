using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Functions
{
    /// <summary>
    /// API for managing IME log patterns (portal-facing, JWT auth).
    /// Phase 1: Read-only for tenants, edit/reseed for Galactic Admins only.
    /// </summary>
    public class ImeLogPatternsFunction
    {
        private readonly ILogger<ImeLogPatternsFunction> _logger;
        private readonly ImeLogPatternService _patternService;
        private readonly GalacticAdminService _galacticAdminService;

        public ImeLogPatternsFunction(ILogger<ImeLogPatternsFunction> logger, ImeLogPatternService patternService, GalacticAdminService galacticAdminService)
        {
            _logger = logger;
            _patternService = patternService;
            _galacticAdminService = galacticAdminService;
        }

        [Function("GetImeLogPatterns")]
        public async Task<HttpResponseData> GetPatterns(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "rules/ime-log-patterns")] HttpRequestData req)
        {
            var tenantId = TenantHelper.IsAuthenticated(req) ? TenantHelper.GetTenantId(req) : null;
            if (string.IsNullOrEmpty(tenantId))
            {
                var unauthorized = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauthorized.WriteAsJsonAsync(new { success = false, message = "Authentication required" });
                return unauthorized;
            }

            var patterns = await _patternService.GetAllPatternsForTenantAsync(tenantId);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { success = true, patterns });
            return response;
        }

        [Function("UpdateImeLogPattern")]
        public async Task<HttpResponseData> UpdatePattern(
            [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "rules/ime-log-patterns/{patternId}")] HttpRequestData req,
            string patternId)
        {
            var tenantId = TenantHelper.IsAuthenticated(req) ? TenantHelper.GetTenantId(req) : null;
            if (string.IsNullOrEmpty(tenantId))
            {
                var unauthorized = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauthorized.WriteAsJsonAsync(new { success = false, message = "Authentication required" });
                return unauthorized;
            }

            // Phase 1: Only global edits by Galactic Admins
            var globalEdit = req.Url.Query.Contains("global=true", StringComparison.OrdinalIgnoreCase);
            if (!globalEdit)
            {
                var forbidden = req.CreateResponse(HttpStatusCode.Forbidden);
                await forbidden.WriteAsJsonAsync(new { success = false, message = "IME log pattern edits require ?global=true and Galactic Admin privileges" });
                return forbidden;
            }

            var upn = TenantHelper.GetUserIdentifier(req);
            var isGalactic = await _galacticAdminService.IsGalacticAdminAsync(upn);
            if (!isGalactic)
            {
                var forbidden = req.CreateResponse(HttpStatusCode.Forbidden);
                await forbidden.WriteAsJsonAsync(new { success = false, message = "Galactic Admin privileges required" });
                return forbidden;
            }

            if (req.Headers.TryGetValues("Content-Length", out var clValues)
                && long.TryParse(clValues.FirstOrDefault(), out var contentLength)
                && contentLength > 1_048_576)
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { success = false, message = "Request body too large" });
                return badRequest;
            }
            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var pattern = JsonConvert.DeserializeObject<ImeLogPattern>(body);

            if (pattern == null)
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { success = false, message = "Invalid pattern data" });
                return badRequest;
            }

            pattern.PatternId = patternId;

            var success = await _patternService.UpdateGlobalPatternAsync(pattern);
            var response = req.CreateResponse(success ? HttpStatusCode.OK : HttpStatusCode.InternalServerError);
            await response.WriteAsJsonAsync(new { success, message = success ? "Global pattern updated" : "Failed to update global pattern" });
            return response;
        }

        [Function("ReseedImeLogPatterns")]
        public async Task<HttpResponseData> ReseedPatterns(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "rules/ime-log-patterns/reseed")] HttpRequestData req)
        {
            try
            {
                if (!TenantHelper.IsAuthenticated(req))
                {
                    var unauthorized = req.CreateResponse(HttpStatusCode.Unauthorized);
                    await unauthorized.WriteAsJsonAsync(new { success = false, message = "Authentication required" });
                    return unauthorized;
                }

                var upn = TenantHelper.GetUserIdentifier(req);
                if (!await _galacticAdminService.IsGalacticAdminAsync(upn))
                {
                    var forbidden = req.CreateResponse(HttpStatusCode.Forbidden);
                    await forbidden.WriteAsJsonAsync(new { success = false, message = "Galactic Admin privileges required" });
                    return forbidden;
                }

                _logger.LogInformation($"Reseed IME log patterns triggered by Galactic Admin {upn}");

                var (deleted, written) = await _patternService.ReseedBuiltInPatternsAsync();

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    message = $"Reseed complete: {deleted} old patterns removed, {written} patterns written from code.",
                    deleted,
                    written
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reseeding IME log patterns");
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { success = false, message = "Failed to reseed IME log patterns" });
                return response;
            }
        }
    }
}
