using System.Net;
using System.Security.Cryptography;
using System.Text;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Functions.Functions.Auth;

public class ApiKeyManagementFunction
{
    private readonly ILogger<ApiKeyManagementFunction> _logger;
    private readonly TableStorageService _storageService;
    private readonly GlobalAdminService _globalAdminService;

    public ApiKeyManagementFunction(
        ILogger<ApiKeyManagementFunction> logger,
        TableStorageService storageService,
        GlobalAdminService globalAdminService)
    {
        _logger = logger;
        _storageService = storageService;
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

            var keys = await GetApiKeysForTenantAsync(targetTenantId);

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
            var tableClient = _storageService.GetTableClient(Constants.TableNames.ApiKeys);
            var keys = new List<object>();

            await foreach (var entity in tableClient.QueryAsync<TableEntity>())
            {
                keys.Add(MapKeyEntity(entity));
            }

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
            DateTimeOffset? expiresAt = expiresInDays.HasValue
                ? DateTimeOffset.UtcNow.AddDays(expiresInDays.Value)
                : null;

            var entity = new TableEntity(partitionKey, keyId)
            {
                ["KeyHash"] = keyHash,
                ["Label"] = label,
                ["Scope"] = scope,
                ["TenantId"] = tenantId,
                ["CreatedBy"] = upn,
                ["CreatedAt"] = createdAt,
                ["IsActive"] = true,
                ["RequestCount"] = 0L,
            };
            if (expiresAt.HasValue)
                entity["ExpiresAt"] = expiresAt.Value;

            var tableClient = _storageService.GetTableClient(Constants.TableNames.ApiKeys);
            await tableClient.UpsertEntityAsync(entity);

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

            var tableClient = _storageService.GetTableClient(Constants.TableNames.ApiKeys);

            // Find the key by keyId (RowKey). Try tenant PK first, then GLOBAL.
            TableEntity? foundEntity = null;
            foreach (var partitionKey in new[] { tenantId, "GLOBAL" })
            {
                try
                {
                    var result = await tableClient.GetEntityAsync<TableEntity>(partitionKey, keyId);
                    foundEntity = result.Value;
                    break;
                }
                catch (Azure.RequestFailedException ex) when (ex.Status == 404)
                {
                    // try next
                }
            }

            if (foundEntity == null)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteAsJsonAsync(new { success = false, message = "API key not found" });
                return notFound;
            }

            // Ownership check: non-GA users can only delete their own tenant's keys
            var keyTenantId = foundEntity.GetString("TenantId");
            if (!isGA && !string.Equals(keyTenantId, tenantId, StringComparison.OrdinalIgnoreCase))
            {
                var forbidden = req.CreateResponse(HttpStatusCode.Forbidden);
                await forbidden.WriteAsJsonAsync(new { success = false, message = "Access denied" });
                return forbidden;
            }

            await tableClient.DeleteEntityAsync(foundEntity.PartitionKey, keyId);

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

    private async Task<List<object>> GetApiKeysForTenantAsync(string tenantId)
    {
        var tableClient = _storageService.GetTableClient(Constants.TableNames.ApiKeys);
        var keys = new List<object>();

        await foreach (var entity in tableClient.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '{tenantId}'"))
        {
            keys.Add(MapKeyEntity(entity));
        }

        return keys;
    }

    private static object MapKeyEntity(TableEntity entity)
    {
        return new
        {
            keyId = entity.RowKey,
            scope = entity.GetString("Scope") ?? "tenant",
            tenantId = entity.GetString("TenantId") ?? entity.PartitionKey,
            label = entity.GetString("Label") ?? "",
            createdBy = entity.GetString("CreatedBy") ?? "",
            createdAt = entity.GetDateTimeOffset("CreatedAt")?.UtcDateTime,
            expiresAt = entity.GetDateTimeOffset("ExpiresAt")?.UtcDateTime,
            isActive = entity.GetBoolean("IsActive") ?? true,
            requestCount = entity.TryGetValue("RequestCount", out var rc) ? Convert.ToInt64(rc) : 0L,
        };
    }
}
