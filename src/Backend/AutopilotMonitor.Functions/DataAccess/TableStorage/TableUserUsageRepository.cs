using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Table Storage implementation of IUserUsageRepository.
    /// PartitionKey: userId (oid claim), RowKey: {yyyyMMdd}_{normalizedEndpoint}
    /// </summary>
    public class TableUserUsageRepository : IUserUsageRepository
    {
        private readonly TableClient _tableClient;
        private readonly ILogger<TableUserUsageRepository> _logger;

        public TableUserUsageRepository(
            TableStorageService storage,
            ILogger<TableUserUsageRepository> logger)
        {
            _logger = logger;
            _tableClient = storage.GetTableClient(Constants.TableNames.UserUsageLog);
        }

        public async Task IncrementUsageAsync(string userId, string userPrincipalName, string tenantId, string endpoint)
        {
            var date = DateTime.UtcNow.ToString("yyyyMMdd");
            var rowKey = $"{date}_{endpoint}";

            const int maxRetries = 3;
            for (var attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    var result = await _tableClient.GetEntityAsync<TableEntity>(userId, rowKey);
                    var entity = result.Value;
                    var count = entity.TryGetValue("RequestCount", out var c) ? Convert.ToInt64(c) : 0L;
                    entity["RequestCount"] = count + 1;
                    entity["LastRequestAt"] = DateTimeOffset.UtcNow;
                    await _tableClient.UpdateEntityAsync(entity, entity.ETag);
                    return;
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    var entity = new TableEntity(userId, rowKey)
                    {
                        ["Date"] = date,
                        ["Endpoint"] = endpoint,
                        ["UserId"] = userId,
                        ["UserPrincipalName"] = userPrincipalName,
                        ["TenantId"] = tenantId,
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
                        continue;
                    }
                }
                catch (RequestFailedException ex) when (ex.Status == 412)
                {
                    continue;
                }
            }

            _logger.LogDebug("Failed to increment user usage after {MaxRetries} retries: user={UserId}, endpoint={Endpoint}",
                maxRetries, userId, endpoint);
        }

        public async Task<List<UserUsageRecord>> GetUsageByUserAsync(string userId, string? dateFrom = null, string? dateTo = null)
        {
            var filter = $"PartitionKey eq '{userId}'";
            filter = AppendDateFilter(filter, dateFrom, dateTo);

            var records = new List<UserUsageRecord>();
            await foreach (var entity in _tableClient.QueryAsync<TableEntity>(filter: filter))
            {
                records.Add(MapToRecord(entity));
            }
            return records;
        }

        public async Task<List<UserUsageRecord>> GetUsageByTenantAsync(string tenantId, string? dateFrom = null, string? dateTo = null)
        {
            var filter = $"TenantId eq '{tenantId}'";
            filter = AppendDateFilter(filter, dateFrom, dateTo);

            var records = new List<UserUsageRecord>();
            await foreach (var entity in _tableClient.QueryAsync<TableEntity>(filter: filter))
            {
                records.Add(MapToRecord(entity));
            }
            return records;
        }

        public async Task<List<UserUsageDailySummary>> GetDailySummaryAsync(string? tenantId = null, string? dateFrom = null, string? dateTo = null)
        {
            string? filter = null;
            if (!string.IsNullOrEmpty(tenantId))
                filter = $"TenantId eq '{tenantId}'";
            filter = AppendDateFilter(filter, dateFrom, dateTo);

            var records = new List<UserUsageRecord>();
            await foreach (var entity in _tableClient.QueryAsync<TableEntity>(filter: filter))
            {
                records.Add(MapToRecord(entity));
            }

            var grouped = records
                .GroupBy(r => r.Date)
                .Select(g => new UserUsageDailySummary
                {
                    Date = g.Key,
                    TenantId = tenantId,
                    TotalRequests = g.Sum(r => r.RequestCount),
                    UniqueUsers = g.Select(r => r.UserId).Distinct().Count(),
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

        private static UserUsageRecord MapToRecord(TableEntity entity)
        {
            return new UserUsageRecord
            {
                UserId = entity.GetString("UserId") ?? entity.PartitionKey,
                UserPrincipalName = entity.GetString("UserPrincipalName") ?? string.Empty,
                TenantId = entity.GetString("TenantId") ?? string.Empty,
                Endpoint = entity.GetString("Endpoint") ?? string.Empty,
                Date = entity.GetString("Date") ?? string.Empty,
                RequestCount = entity.TryGetValue("RequestCount", out var rc) ? Convert.ToInt64(rc) : 0L,
                LastRequestAt = entity.GetDateTimeOffset("LastRequestAt")?.UtcDateTime ?? DateTime.MinValue,
            };
        }
    }
}
