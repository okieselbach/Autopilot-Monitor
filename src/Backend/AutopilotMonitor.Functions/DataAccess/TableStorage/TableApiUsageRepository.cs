using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Table Storage implementation of IApiUsageRepository.
    /// PartitionKey: keyId, RowKey: {yyyyMMdd}_{normalizedEndpoint}
    /// </summary>
    public class TableApiUsageRepository : IApiUsageRepository
    {
        private readonly TableClient _tableClient;
        private readonly ILogger<TableApiUsageRepository> _logger;

        public TableApiUsageRepository(
            TableStorageService storage,
            ILogger<TableApiUsageRepository> logger)
        {
            _logger = logger;
            _tableClient = storage.GetTableClient(Constants.TableNames.ApiUsageLog);
        }

        public async Task IncrementUsageAsync(string keyId, string tenantId, string scope, string endpoint)
        {
            var date = DateTime.UtcNow.ToString("yyyyMMdd");
            var rowKey = $"{date}_{endpoint}";

            const int maxRetries = 3;
            for (var attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    var result = await _tableClient.GetEntityAsync<TableEntity>(keyId, rowKey);
                    var entity = result.Value;
                    var count = entity.TryGetValue("RequestCount", out var c) ? Convert.ToInt64(c) : 0L;
                    entity["RequestCount"] = count + 1;
                    entity["LastRequestAt"] = DateTimeOffset.UtcNow;
                    await _tableClient.UpdateEntityAsync(entity, entity.ETag);
                    return;
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    // Entity doesn't exist yet — create it
                    var entity = new TableEntity(keyId, rowKey)
                    {
                        ["Date"] = date,
                        ["Endpoint"] = endpoint,
                        ["KeyId"] = keyId,
                        ["TenantId"] = tenantId,
                        ["Scope"] = scope,
                        ["RequestCount"] = 1L,
                        ["LastRequestAt"] = DateTimeOffset.UtcNow,
                    };
                    try
                    {
                        await _tableClient.AddEntityAsync(entity);
                        return;
                    }
                    catch (RequestFailedException addEx) when (addEx.Status == 409)
                    {
                        // Another request created it concurrently — retry the update
                        continue;
                    }
                }
                catch (RequestFailedException ex) when (ex.Status == 412)
                {
                    // ETag conflict — another request updated concurrently, retry
                    continue;
                }
            }

            _logger.LogDebug("Failed to increment usage after {MaxRetries} retries: key={KeyId}, endpoint={Endpoint}",
                maxRetries, keyId, endpoint);
        }

        public async Task<List<ApiUsageRecord>> GetUsageByKeyAsync(string keyId, string? dateFrom = null, string? dateTo = null)
        {
            var filter = $"PartitionKey eq '{keyId}'";
            filter = AppendDateFilter(filter, dateFrom, dateTo);

            var records = new List<ApiUsageRecord>();
            await foreach (var entity in _tableClient.QueryAsync<TableEntity>(filter: filter))
            {
                records.Add(MapToRecord(entity));
            }
            return records;
        }

        public async Task<List<ApiUsageRecord>> GetUsageByTenantAsync(string tenantId, string? dateFrom = null, string? dateTo = null)
        {
            var filter = $"TenantId eq '{tenantId}'";
            filter = AppendDateFilter(filter, dateFrom, dateTo);

            var records = new List<ApiUsageRecord>();
            await foreach (var entity in _tableClient.QueryAsync<TableEntity>(filter: filter))
            {
                records.Add(MapToRecord(entity));
            }
            return records;
        }

        public async Task<List<ApiUsageDailySummary>> GetDailySummaryAsync(string? tenantId = null, string? dateFrom = null, string? dateTo = null)
        {
            string? filter = null;
            if (!string.IsNullOrEmpty(tenantId))
                filter = $"TenantId eq '{tenantId}'";
            filter = AppendDateFilter(filter, dateFrom, dateTo);

            var records = new List<ApiUsageRecord>();
            await foreach (var entity in _tableClient.QueryAsync<TableEntity>(filter: filter))
            {
                records.Add(MapToRecord(entity));
            }

            // Aggregate by date
            var grouped = records
                .GroupBy(r => r.Date)
                .Select(g => new ApiUsageDailySummary
                {
                    Date = g.Key,
                    TenantId = tenantId,
                    TotalRequests = g.Sum(r => r.RequestCount),
                    UniqueKeys = g.Select(r => r.KeyId).Distinct().Count(),
                    UniqueEndpoints = g.Select(r => r.Endpoint).Distinct().Count(),
                })
                .OrderByDescending(s => s.Date)
                .ToList();

            return grouped;
        }

        private static string AppendDateFilter(string? existingFilter, string? dateFrom, string? dateTo)
        {
            var filter = existingFilter ?? "";

            if (!string.IsNullOrEmpty(dateFrom))
            {
                var dateVal = dateFrom.Replace("-", "");
                var clause = $"Date ge '{dateVal}'";
                filter = string.IsNullOrEmpty(filter) ? clause : $"{filter} and {clause}";
            }

            if (!string.IsNullOrEmpty(dateTo))
            {
                var dateVal = dateTo.Replace("-", "");
                var clause = $"Date le '{dateVal}'";
                filter = string.IsNullOrEmpty(filter) ? clause : $"{filter} and {clause}";
            }

            return string.IsNullOrEmpty(filter) ? null! : filter;
        }

        private static ApiUsageRecord MapToRecord(TableEntity entity)
        {
            return new ApiUsageRecord
            {
                KeyId = entity.GetString("KeyId") ?? entity.PartitionKey,
                TenantId = entity.GetString("TenantId") ?? string.Empty,
                Scope = entity.GetString("Scope") ?? string.Empty,
                Endpoint = entity.GetString("Endpoint") ?? string.Empty,
                Date = entity.GetString("Date") ?? string.Empty,
                RequestCount = entity.TryGetValue("RequestCount", out var rc) ? Convert.ToInt64(rc) : 0L,
                LastRequestAt = entity.GetDateTimeOffset("LastRequestAt")?.UtcDateTime ?? DateTime.MinValue,
            };
        }
    }
}
