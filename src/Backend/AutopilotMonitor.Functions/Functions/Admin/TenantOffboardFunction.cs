using System.Net;
using AutopilotMonitor.Functions.Extensions;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Admin;

/// <summary>
/// Tenant Offboarding endpoint
/// Allows a Tenant Admin to permanently delete all data for their tenant.
/// This operation is irreversible.
/// </summary>
public class TenantOffboardFunction
{
    private readonly ILogger<TenantOffboardFunction> _logger;
    private readonly TenantAdminsService _tenantAdminsService;
    private readonly GalacticAdminService _galacticAdminService;
    private readonly TableServiceClient _tableServiceClient;

    public TenantOffboardFunction(
        ILogger<TenantOffboardFunction> logger,
        TenantAdminsService tenantAdminsService,
        GalacticAdminService galacticAdminService,
        IConfiguration configuration)
    {
        _logger = logger;
        _tenantAdminsService = tenantAdminsService;
        _galacticAdminService = galacticAdminService;
        var connectionString = configuration["AzureTableStorageConnectionString"];
        _tableServiceClient = new TableServiceClient(connectionString);
    }

    /// <summary>
    /// DELETE /api/tenants/{tenantId}/offboard
    /// Permanently deletes ALL data for a tenant across all tables.
    /// Accessible by: Tenant Admins of the same tenant OR Galactic Admins.
    /// This action is IRREVERSIBLE.
    /// </summary>
    [Function("OffboardTenant")]
    [Authorize]
    public async Task<HttpResponseData> OffboardTenant(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "tenants/{tenantId}/offboard")] HttpRequestData req,
        string tenantId,
        FunctionContext context)
    {
        // Authentication enforced by PolicyEnforcementMiddleware
        var principal = context.GetUser()!;

        var userTenantId = principal.GetTenantId();
        var upn = principal.GetUserPrincipalName();

        // Only Tenant Admins of the same tenant OR Galactic Admins may offboard
        var isGalacticAdmin = await _galacticAdminService.IsGalacticAdminAsync(upn);
        var isTenantAdmin = tenantId.Equals(userTenantId, StringComparison.OrdinalIgnoreCase) &&
                            await _tenantAdminsService.IsTenantAdminAsync(tenantId, upn);

        if (!isGalacticAdmin && !isTenantAdmin)
        {
            _logger.LogWarning($"User {upn} attempted to offboard tenant {tenantId} without authorization");
            var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
            await forbiddenResponse.WriteAsJsonAsync(new
            {
                error = "Access denied. Only a Tenant Admin of this tenant may perform offboarding."
            });
            return forbiddenResponse;
        }

        // Validate tenantId format to prevent OData injection
        SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

        _logger.LogWarning($"TENANT OFFBOARD initiated for tenant {tenantId} by {upn}");

        var result = new OffboardResult { TenantId = tenantId, InitiatedBy = upn!, InitiatedAt = DateTime.UtcNow };

        try
        {
            // Tables where PartitionKey = tenantId (normalized lowercase)
            var tenantPartitionTables = new[]
            {
                Constants.TableNames.Sessions,
                Constants.TableNames.SessionsIndex,
                Constants.TableNames.AuditLogs,
                Constants.TableNames.UsageMetrics,
                Constants.TableNames.UserActivity,
                Constants.TableNames.GatherRules,
                Constants.TableNames.AnalyzeRules,
                Constants.TableNames.AppInstallSummaries,
                Constants.TableNames.TenantConfiguration,
                Constants.TableNames.TenantAdmins,
                Constants.TableNames.BootstrapSessions,
                Constants.TableNames.BlockedDevices,
                Constants.TableNames.ImeLogPatterns,
                Constants.TableNames.RuleStates,
            };

            var normalizedTenantId = tenantId.ToLowerInvariant();

            foreach (var tableName in tenantPartitionTables)
            {
                var deleted = await DeleteAllRowsByPartitionKeyAsync(tableName, normalizedTenantId);
                result.DeletedCounts[tableName] = deleted;
                _logger.LogInformation($"Offboard [{tenantId}] {tableName}: deleted {deleted} rows");
            }

            // Tables with composite PartitionKey = "{tenantId}_{sessionId}" – query by prefix
            var eventsDeleted = await DeleteByTenantPrefixAsync(Constants.TableNames.Events, normalizedTenantId);
            result.DeletedCounts[Constants.TableNames.Events] = eventsDeleted;
            _logger.LogInformation($"Offboard [{tenantId}] Events: deleted {eventsDeleted} rows");

            var ruleResultsDeleted = await DeleteByTenantPrefixAsync(Constants.TableNames.RuleResults, normalizedTenantId);
            result.DeletedCounts[Constants.TableNames.RuleResults] = ruleResultsDeleted;
            _logger.LogInformation($"Offboard [{tenantId}] RuleResults: deleted {ruleResultsDeleted} rows");

            // BootstrapSessions CodeLookup entries (PartitionKey = "CodeLookup", TenantId property = tenantId)
            var codeLookupDeleted = await DeleteCodeLookupEntriesAsync(normalizedTenantId);
            result.DeletedCounts["BootstrapSessions_CodeLookup"] = codeLookupDeleted;
            _logger.LogInformation($"Offboard [{tenantId}] BootstrapSessions CodeLookup: deleted {codeLookupDeleted} rows");

            result.Success = true;
            _logger.LogWarning($"TENANT OFFBOARD completed for tenant {tenantId} by {upn}. " +
                $"Total rows deleted: {result.DeletedCounts.Values.Sum()}");

            var okResponse = req.CreateResponse(HttpStatusCode.OK);
            await okResponse.WriteAsJsonAsync(result);
            return okResponse;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Tenant offboard failed for tenant {tenantId}");
            result.Success = false;
            result.Error = "Internal server error";

            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(result);
            return errorResponse;
        }
    }

    /// <summary>
    /// Deletes all rows in a table where PartitionKey equals the given value.
    /// Uses batched deletes (max 100 per transaction, same PartitionKey required).
    /// </summary>
    private async Task<int> DeleteAllRowsByPartitionKeyAsync(string tableName, string partitionKey)
    {
        try
        {
            var tableClient = _tableServiceClient.GetTableClient(tableName);
            var filter = $"PartitionKey eq '{partitionKey}'";
            var entities = tableClient.QueryAsync<TableEntity>(filter, select: new[] { "PartitionKey", "RowKey" });

            int deleted = 0;
            var batch = new List<TableTransactionAction>();

            await foreach (var entity in entities)
            {
                batch.Add(new TableTransactionAction(TableTransactionActionType.Delete, entity));
                deleted++;

                if (batch.Count >= 100)
                {
                    await tableClient.SubmitTransactionAsync(batch);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                await tableClient.SubmitTransactionAsync(batch);
            }

            return deleted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to delete rows in {tableName} for partition {partitionKey}");
            return 0;
        }
    }

    /// <summary>
    /// Deletes all rows for a tenant from a table that uses composite PartitionKey = "{tenantId}_{sessionId}".
    /// Used for Events and RuleResults tables.
    /// </summary>
    private async Task<int> DeleteByTenantPrefixAsync(string tableName, string normalizedTenantId)
    {
        try
        {
            var tableClient = _tableServiceClient.GetTableClient(tableName);
            // OData startsWith: PartitionKey ge 'prefix' and PartitionKey lt 'prefix~'
            var prefix = normalizedTenantId + "_";
            var filter = $"PartitionKey ge '{prefix}' and PartitionKey lt '{prefix}~'";
            var entities = tableClient.QueryAsync<TableEntity>(filter, select: new[] { "PartitionKey", "RowKey" });

            int deleted = 0;
            // Group by PartitionKey because Azure Table batch transactions require same PartitionKey
            var groups = new Dictionary<string, List<TableEntity>>();

            await foreach (var entity in entities)
            {
                if (!groups.ContainsKey(entity.PartitionKey))
                    groups[entity.PartitionKey] = new List<TableEntity>();
                groups[entity.PartitionKey].Add(entity);
                deleted++;
            }

            foreach (var group in groups.Values)
            {
                for (int i = 0; i < group.Count; i += 100)
                {
                    var chunk = group.Skip(i).Take(100).ToList();
                    var batch = chunk.Select(e => new TableTransactionAction(TableTransactionActionType.Delete, e)).ToList();
                    await tableClient.SubmitTransactionAsync(batch);
                }
            }

            return deleted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to delete {tableName} for tenant {normalizedTenantId}");
            return 0;
        }
    }

    /// <summary>
    /// Deletes BootstrapSessions CodeLookup entries for a tenant.
    /// These use PartitionKey = "CodeLookup" with a TenantId property matching the tenant.
    /// </summary>
    private async Task<int> DeleteCodeLookupEntriesAsync(string normalizedTenantId)
    {
        try
        {
            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.BootstrapSessions);
            var filter = $"PartitionKey eq 'CodeLookup' and TenantId eq '{normalizedTenantId}'";
            var entities = tableClient.QueryAsync<TableEntity>(filter, select: new[] { "PartitionKey", "RowKey" });

            int deleted = 0;
            var batch = new List<TableTransactionAction>();

            await foreach (var entity in entities)
            {
                batch.Add(new TableTransactionAction(TableTransactionActionType.Delete, entity));
                deleted++;

                if (batch.Count >= 100)
                {
                    await tableClient.SubmitTransactionAsync(batch);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                await tableClient.SubmitTransactionAsync(batch);
            }

            return deleted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to delete CodeLookup entries for tenant {normalizedTenantId}");
            return 0;
        }
    }
}

public class OffboardResult
{
    public string TenantId { get; set; } = string.Empty;
    public string InitiatedBy { get; set; } = string.Empty;
    public DateTime InitiatedAt { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public Dictionary<string, int> DeletedCounts { get; set; } = new();
}
