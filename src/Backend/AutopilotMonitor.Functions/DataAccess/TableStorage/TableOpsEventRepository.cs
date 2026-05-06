using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Pagination;
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

        public async Task<List<OpsEventEntry>> GetOpsEventsAsync(
            string? category = null, DateTime? dateFrom = null, DateTime? dateTo = null)
        {
            var result = new List<OpsEventEntry>();
            var filter = BuildFilter(category, dateFrom, dateTo);
            var query = string.IsNullOrEmpty(filter)
                ? _table.QueryAsync<TableEntity>()
                : _table.QueryAsync<TableEntity>(filter: filter);

            await foreach (var entity in query)
            {
                result.Add(MapToEntry(entity));
            }
            // Cross-partition order is undefined; sort newest-first to match prior behaviour.
            result.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));
            return result;
        }

        public async Task<RawPage<OpsEventEntry>> GetOpsEventsPageAsync(
            string? category, DateTime? dateFrom, DateTime? dateTo, int pageSize, string? continuation)
        {
            if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));
            try
            {
                // Single-category path: PK-targeted query, RowKey is reverse-tick →
                // Azure-native (PK asc, RK asc) already yields newest-first. No re-sort.
                if (!string.IsNullOrEmpty(category))
                {
                    var filter = BuildFilter(category, dateFrom, dateTo);
                    var (entities, nextRawToken) = await AzureTablesPaginator.FetchPageAsync<TableEntity>(
                        client: _table,
                        filter: filter,
                        pageSize: pageSize,
                        continuation: continuation);

                    var page = new List<OpsEventEntry>(entities.Count);
                    foreach (var entity in entities) page.Add(MapToEntry(entity));
                    return new RawPage<OpsEventEntry>(page, nextRawToken);
                }

                // All-category path: per-partition fan-out + merge-sort. Azure pages
                // cross-partition queries by (PK asc, RK asc), so without this fan-out
                // the first page would come entirely from the alphabetically-first
                // category — defeating "newest first globally". Mirrors the same
                // pattern used for cross-tenant session queries.
                return await FanOutAcrossCategoriesAsync(dateFrom, dateTo, pageSize, continuation);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get ops events page");
                return RawPage<OpsEventEntry>.Empty;
            }
        }

        // Fixed list — keep in sync with OpsEventCategory. New categories are rare
        // and require a deploy anyway, so a runtime probe of the table partitions
        // would just trade certainty for cost.
        internal static readonly string[] AllCategories = new[]
        {
            OpsEventCategory.Consent,
            OpsEventCategory.Maintenance,
            OpsEventCategory.Security,
            OpsEventCategory.Tenant,
            OpsEventCategory.Agent,
            OpsEventCategory.Sla,
        };

        private async Task<RawPage<OpsEventEntry>> FanOutAcrossCategoriesAsync(
            DateTime? dateFrom, DateTime? dateTo, int pageSize, string? continuation)
        {
            var continuations = PerPartitionFanOutMerge.DecodeMultiContinuation(continuation);

            var activeCats = AllCategories
                .Where(cat => !(continuations.TryGetValue(cat, out var c) && c.Exhausted))
                .ToList();
            if (activeCats.Count == 0)
                return new RawPage<OpsEventEntry>(new List<OpsEventEntry>(), null);

            var fetchTasks = activeCats.Select(async cat =>
            {
                continuations.TryGetValue(cat, out var catContinuation);
                var filter = BuildFilterWithRowKeyBound(cat, dateFrom, dateTo, catContinuation?.LastRowKey);

                var fetched = new List<(string RowKey, OpsEventEntry Item)>();
                await foreach (var e in _table.QueryAsync<TableEntity>(filter: filter, maxPerPage: pageSize))
                {
                    fetched.Add((e.RowKey, MapToEntry(e)));
                    if (fetched.Count >= pageSize) break;
                }
                return new PerPartitionFanOutMerge.PartitionFetchResult<OpsEventEntry>(cat, fetched);
            }).ToList();

            var results = await Task.WhenAll(fetchTasks);

            var (items, nextContinuations) = PerPartitionFanOutMerge.MergeAndAdvance(
                results, continuations, pageSize, e => e.Timestamp);

            bool anyActive = nextContinuations.Any(c => !c.Value.Exhausted);
            string? nextRawToken = anyActive
                ? PerPartitionFanOutMerge.EncodeMultiContinuation(nextContinuations)
                : null;
            return new RawPage<OpsEventEntry>(items, nextRawToken);
        }

        private static string BuildFilterWithRowKeyBound(string category, DateTime? dateFrom, DateTime? dateTo, string? lastRowKey)
        {
            var clauses = new List<string>
            {
                $"PartitionKey eq '{category.Replace("'", "''")}'",
            };
            if (!string.IsNullOrEmpty(lastRowKey))
                clauses.Add($"RowKey gt '{lastRowKey!.Replace("'", "''")}'");
            if (dateFrom.HasValue)
                clauses.Add($"Timestamp ge datetime'{ToUtc(dateFrom.Value):o}'");
            if (dateTo.HasValue)
                clauses.Add($"Timestamp le datetime'{ToUtc(dateTo.Value):o}'");
            return string.Join(" and ", clauses);
        }

        // Filters on the system-managed Timestamp; user-defined "Timestamp" property
        // mirrors it on insert via SaveOpsEventAsync.
        private static string? BuildFilter(string? category, DateTime? dateFrom, DateTime? dateTo)
        {
            var clauses = new List<string>();
            if (!string.IsNullOrEmpty(category))
            {
                clauses.Add($"PartitionKey eq '{category!.Replace("'", "''")}'");
            }
            if (dateFrom.HasValue)
            {
                clauses.Add($"Timestamp ge datetime'{ToUtc(dateFrom.Value):o}'");
            }
            if (dateTo.HasValue)
            {
                clauses.Add($"Timestamp le datetime'{ToUtc(dateTo.Value):o}'");
            }
            return clauses.Count == 0 ? null : string.Join(" and ", clauses);
        }

        private static DateTime ToUtc(DateTime dt)
            => dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();

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
