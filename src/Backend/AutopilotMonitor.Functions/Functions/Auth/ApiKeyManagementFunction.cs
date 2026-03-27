using System.Net;
using System.Security.Cryptography;
using System.Text;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Functions.Functions.Auth;

public class ApiKeyManagementFunction
{
    private readonly ILogger<ApiKeyManagementFunction> _logger;
    private readonly IAdminRepository _adminRepo;
    private readonly GlobalAdminService _globalAdminService;

    public ApiKeyManagementFunction(
        ILogger<ApiKeyManagementFunction> logger,
        IAdminRepository adminRepo,
        GlobalAdminService globalAdminService)
    {
        _logger = logger;
        _adminRepo = adminRepo;
        _globalAdminService = globalAdminService;
    }

    /// <summary>GET /api/api-keys — list API keys for the calling tenant</summary>
    [Function("ListApiKeys")]
    public async Task<HttpResponseData> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "api-keys")] HttpRequestData req)
    {
        try
        {
            var tenantId = TenantHelper.GetTenantId(req);
            var upn = TenantHelper.GetUserIdentifier(req);
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);

            // GA can query other tenants
            string targetTenantId = tenantId;
            if (await _globalAdminService.IsGlobalAdminAsync(upn))
            {
                var overrideTenant = query["tenantId"];
                if (!string.IsNullOrEmpty(overrideTenant))
                    targetTenantId = overrideTenant;
            }

            var entries = await _adminRepo.GetApiKeysAsync(targetTenantId);
            var keys = entries.Select(MapKeyEntry).ToList();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { success = true, count = keys.Count, keys });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing API keys");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
            return err;
        }
    }

    /// <summary>GET /api/global/api-keys — list all API keys (GlobalAdmin only)</summary>
    [Function("ListApiKeysGlobal")]
    public async Task<HttpResponseData> ListGlobal(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/api-keys")] HttpRequestData req)
    {
        try
        {
            var entries = await _adminRepo.GetAllApiKeysAsync();
            var keys = entries.Select(MapKeyEntry).ToList();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { success = true, count = keys.Count, keys });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing all API keys");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
            return err;
        }
    }

    /// <summary>POST /api/api-keys — create a new API key</summary>
    [Function("CreateApiKey")]
    public async Task<HttpResponseData> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "api-keys")] HttpRequestData req)
    {
        try
        {
            var tenantId = TenantHelper.GetTenantId(req);
            var upn = TenantHelper.GetUserIdentifier(req);
            var isGA = await _globalAdminService.IsGlobalAdminAsync(upn);

            string body;
            using (var reader = new System.IO.StreamReader(req.Body))
                body = await reader.ReadToEndAsync();

            JObject json;
            try { json = JObject.Parse(body); }
            catch
            {
                var br = req.CreateResponse(HttpStatusCode.BadRequest);
                await br.WriteAsJsonAsync(new { success = false, message = "Invalid JSON body" });
                return br;
            }

            var label = json["label"]?.ToString() ?? "API Key";
            var scope = json["scope"]?.ToString() ?? "tenant";
            var expiresInDays = json["expiresInDays"]?.Value<int?>();

            // Only global admins can create global-scoped keys
            if (scope == "global" && !isGA)
            {
                var forbidden = req.CreateResponse(HttpStatusCode.Forbidden);
                await forbidden.WriteAsJsonAsync(new { success = false, message = "Only Global Admins can create global-scoped API keys" });
                return forbidden;
            }

            // Generate a 32-byte random key
            var rawKeyBytes = RandomNumberGenerator.GetBytes(32);
            var rawKey = Convert.ToHexString(rawKeyBytes).ToLowerInvariant();

            // SHA-256 hash — only stored value
            using var sha = SHA256.Create();
            var hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(rawKey));
            var keyHash = Convert.ToHexString(hashBytes).ToLowerInvariant();

            var keyId = Guid.NewGuid().ToString();
            var partitionKey = scope == "global" ? "GLOBAL" : tenantId;
            var createdAt = DateTime.UtcNow;
            DateTime? expiresAt = expiresInDays.HasValue
                ? DateTime.UtcNow.AddDays(expiresInDays.Value)
                : null;

            var entry = new ApiKeyEntry
            {
                KeyId = keyId,
                TenantId = tenantId,
                KeyHash = keyHash,
                Name = label,
                Scope = scope,
                Upn = upn,
                CreatedBy = upn,
                CreatedAt = createdAt,
                ExpiresAt = expiresAt,
                IsActive = true,
                RequestCount = 0L,
            };

            await _adminRepo.StoreApiKeyAsync(partitionKey, entry);

            _logger.LogInformation("API key created: scope={Scope}, tenantId={TenantId}, createdBy={CreatedBy}", scope, tenantId, upn);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                keyId,
                rawKey, // Only returned on creation — never again
                scope,
                label,
                tenantId,
                createdAt,
                expiresAt
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating API key");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
            return err;
        }
    }

    /// <summary>DELETE /api/api-keys/{keyId} — revoke an API key</summary>
    [Function("DeleteApiKey")]
    public async Task<HttpResponseData> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "api-keys/{keyId}")] HttpRequestData req,
        string keyId)
    {
        try
        {
            var tenantId = TenantHelper.GetTenantId(req);
            var upn = TenantHelper.GetUserIdentifier(req);
            var isGA = await _globalAdminService.IsGlobalAdminAsync(upn);

            // Find the key by keyId. Try tenant PK first, then GLOBAL.
            ApiKeyEntry? foundEntry = null;
            string? foundPartitionKey = null;
            foreach (var partitionKey in new[] { tenantId, "GLOBAL" })
            {
                var entry = await _adminRepo.GetApiKeyAsync(partitionKey, keyId);
                if (entry != null)
                {
                    foundEntry = entry;
                    foundPartitionKey = partitionKey;
                    break;
                }
            }

            if (foundEntry == null)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteAsJsonAsync(new { success = false, message = "API key not found" });
                return notFound;
            }

            // Ownership check: non-GA users can only delete their own tenant's keys
            if (!isGA && !string.Equals(foundEntry.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
            {
                var forbidden = req.CreateResponse(HttpStatusCode.Forbidden);
                await forbidden.WriteAsJsonAsync(new { success = false, message = "Access denied" });
                return forbidden;
            }

            await _adminRepo.RevokeApiKeyAsync(foundPartitionKey!, keyId);

            _logger.LogInformation("API key deleted: keyId={KeyId}, deletedBy={Upn}", keyId, upn);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { success = true });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting API key {KeyId}", keyId);
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
            return err;
        }
    }

    private static object MapKeyEntry(ApiKeyEntry entry)
    {
        return new
        {
            keyId = entry.KeyId,
            scope = entry.Scope ?? "tenant",
            tenantId = entry.TenantId,
            label = entry.Name ?? "",
            createdBy = entry.CreatedBy ?? "",
            createdAt = entry.CreatedAt,
            expiresAt = entry.ExpiresAt,
            isActive = entry.IsActive,
            requestCount = entry.RequestCount,
        };
    }
}
