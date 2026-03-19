using System;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Functions.Functions.Admin
{
    public class VersionBlockFunction
    {
        private readonly ILogger<VersionBlockFunction> _logger;
        private readonly BlockedVersionService _blockedVersionService;
        private readonly TableStorageService _storageService;

        public VersionBlockFunction(
            ILogger<VersionBlockFunction> logger,
            BlockedVersionService blockedVersionService,
            TableStorageService storageService)
        {
            _logger = logger;
            _blockedVersionService = blockedVersionService;
            _storageService = storageService;
        }

        /// <summary>GET /api/versions/blocked — list all active version block rules</summary>
        [Function("GetBlockedVersions")]
        public async Task<HttpResponseData> GetBlockedVersions(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "versions/blocked")] HttpRequestData req)
        {
            try
            {
                // Authentication + GalacticAdminOnly authorization enforced by PolicyEnforcementMiddleware

                var rules = await _blockedVersionService.GetBlockedVersionsAsync();

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new { success = true, rules });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting blocked versions");
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { error = "Internal server error" });
                return response;
            }
        }

        /// <summary>POST /api/versions/block — add a version block rule</summary>
        [Function("BlockVersion")]
        public async Task<HttpResponseData> BlockVersion(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "versions/block")] HttpRequestData req)
        {
            try
            {
                // Authentication + GalacticAdminOnly authorization enforced by PolicyEnforcementMiddleware
                var userIdentifier = TenantHelper.GetUserIdentifier(req);

                string body;
                using (var reader = new System.IO.StreamReader(req.Body))
                    body = await reader.ReadToEndAsync();

                JObject json;
                try { json = JObject.Parse(body); }
                catch { return await BadRequestAsync(req, "Invalid JSON body"); }

                var versionPattern = json["versionPattern"]?.ToString();
                var action = json["action"]?.ToString() ?? "Block";
                var reason = json["reason"]?.ToString();

                if (string.IsNullOrEmpty(versionPattern))
                    return await BadRequestAsync(req, "versionPattern is required");

                if (!string.Equals(action, "Block", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(action, "Kill", StringComparison.OrdinalIgnoreCase))
                    return await BadRequestAsync(req, "action must be 'Block' or 'Kill'");

                try
                {
                    await _blockedVersionService.BlockVersionAsync(versionPattern, action, userIdentifier, reason);
                }
                catch (ArgumentException ex)
                {
                    return await BadRequestAsync(req, ex.Message);
                }

                var normalizedAction = string.Equals(action, "Kill", StringComparison.OrdinalIgnoreCase) ? "Kill" : "Block";

                await _storageService.LogAuditEntryAsync(
                    AutopilotMonitor.Shared.Constants.AuditGlobalTenantId,
                    "CREATE",
                    "VersionBlock",
                    versionPattern,
                    userIdentifier,
                    new Dictionary<string, string>
                    {
                        { "Action", normalizedAction },
                        { "Reason", reason ?? string.Empty }
                    }
                );

                _logger.LogWarning(
                    "Galactic Admin {User} added version {Action} rule: Pattern={Pattern}, Reason={Reason}",
                    userIdentifier, normalizedAction, versionPattern, reason);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    message = $"Version pattern '{versionPattern}' set to {normalizedAction}.",
                    versionPattern,
                    action = normalizedAction
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding version block rule");
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { error = "Internal server error" });
                return response;
            }
        }

        /// <summary>DELETE /api/versions/block/{encodedPattern} — remove a version block rule</summary>
        [Function("UnblockVersion")]
        public async Task<HttpResponseData> UnblockVersion(
            [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "versions/block/{encodedPattern}")] HttpRequestData req,
            string encodedPattern)
        {
            try
            {
                // Authentication + GalacticAdminOnly authorization enforced by PolicyEnforcementMiddleware
                var userIdentifier = TenantHelper.GetUserIdentifier(req);

                var versionPattern = Uri.UnescapeDataString(encodedPattern ?? string.Empty);
                if (string.IsNullOrEmpty(versionPattern))
                    return await BadRequestAsync(req, "versionPattern is required");

                await _blockedVersionService.UnblockVersionAsync(versionPattern);

                await _storageService.LogAuditEntryAsync(
                    AutopilotMonitor.Shared.Constants.AuditGlobalTenantId,
                    "DELETE",
                    "VersionBlock",
                    versionPattern,
                    userIdentifier
                );

                _logger.LogInformation(
                    "Galactic Admin {User} removed version block rule: Pattern={Pattern}",
                    userIdentifier, versionPattern);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new { success = true, message = $"Version pattern '{versionPattern}' unblocked." });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing version block rule");
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { error = "Internal server error" });
                return response;
            }
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        private static async Task<HttpResponseData> BadRequestAsync(HttpRequestData req, string message)
        {
            var r = req.CreateResponse(HttpStatusCode.BadRequest);
            await r.WriteAsJsonAsync(new { success = false, message });
            return r;
        }
    }
}
