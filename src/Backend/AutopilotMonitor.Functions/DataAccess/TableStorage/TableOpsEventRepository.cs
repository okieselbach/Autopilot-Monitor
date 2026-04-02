using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Table Storage implementation of <see cref="IOpsEventRepository"/>.
    /// PartitionKey = Category (Consent, Maintenance, Security, Tenant, Agent).
    /// RowKey = reverse-tick for newest-first ordering.
    /// </summary>
    public class TableOpsEventRepository : IOpsEventRepository
    {
        private readonly TableClient _table;
        private readonly ILogger<TableOpsEventRepository> _logger;

        public TableOpsEventRepository(
            TableStorageService storage,
            ILogger<TableOpsEventRepository> logger)
        {
            _logger = logger;
            _table = storage.GetTableClient(Constants.TableNames.OpsEvents);
        }

        public async Task SaveOpsEventAsync(OpsEventEntry entry)
        {
            var pk = entry.Category;
            var rk = $"{DateTime.MaxValue.Ticks - DateTime.UtcNow.Ticks:D19}";

            var entity = new TableEntity(pk, rk)
            {
                ["EventType"] = Truncate(entry.EventType, 64),
                ["Severity"]  = Truncate(entry.Severity, 16),
                ["TenantId"]  = Truncate(entry.TenantId, 36),
                ["UserId"]    = Truncate(entry.UserId, 128),
                ["Message"]   = Truncate(entry.Message, 512),
                ["Details"]   = Truncate(entry.Details, 4096),
                ["Timestamp"] = entry.Timestamp,
            };

            await _table.UpsertEntityAsync(entity);
        }

        public async Task<List<OpsEventEntry>> GetOpsEventsAsync(int maxResults = 200)
        {
            var result = new List<OpsEventEntry>();

            // Query all partitions, sort by Timestamp descending client-side
            // (Azure Table Storage doesn't support cross-partition ordering)
            await foreach (var entity in _table.QueryAsync<TableEntity>(maxPerPage: maxResults))
            {
                result.Add(MapToEntry(entity));
                if (result.Count >= maxResults)
                    break;
            }

            // Sort newest-first across categories
            result.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));
            return result;
        }

        public async Task<List<OpsEventEntry>> GetOpsEventsByCategoryAsync(string category, int maxResults = 100)
        {
            var result = new List<OpsEventEntry>();

            await foreach (var entity in _table.QueryAsync<TableEntity>(e => e.PartitionKey == category, maxPerPage: maxResults))
            {
                result.Add(MapToEntry(entity));
                if (result.Count >= maxResults)
                    break;
            }

            return result;
        }

        public async Task<int> DeleteOpsEventsOlderThanAsync(DateTime cutoff)
        {
            var deleted = 0;

            await foreach (var entity in _table.QueryAsync<TableEntity>())
            {
                var timestamp = entity.GetDateTimeOffset("Timestamp")?.UtcDateTime ?? DateTime.MinValue;
                if (timestamp < cutoff)
                {
                    try
                    {
                        await _table.DeleteEntityAsync(entity.PartitionKey, entity.RowKey);
                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete ops event {PK}/{RK}", entity.PartitionKey, entity.RowKey);
                    }
                }
            }

            return deleted;
        }

        private static OpsEventEntry MapToEntry(TableEntity entity)
        {
            return new OpsEventEntry
            {
                Id        = $"{entity.PartitionKey}_{entity.RowKey}",
                Category  = entity.PartitionKey,
                EventType = entity.GetString("EventType") ?? string.Empty,
                Severity  = entity.GetString("Severity") ?? OpsEventSeverity.Info,
                TenantId  = entity.GetString("TenantId"),
                UserId    = entity.GetString("UserId"),
                Message   = entity.GetString("Message") ?? string.Empty,
                Details   = entity.GetString("Details"),
                Timestamp = entity.GetDateTimeOffset("Timestamp")?.UtcDateTime ?? DateTime.MinValue,
            };
        }

        private static string? Truncate(string? value, int maxLength)
        {
            if (value == null) return null;
            return value.Length <= maxLength ? value : value.Substring(0, maxLength);
        }
    }
}
