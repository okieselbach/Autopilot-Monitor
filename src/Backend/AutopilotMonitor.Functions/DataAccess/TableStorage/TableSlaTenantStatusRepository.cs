using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Table Storage implementation of <see cref="ISlaTenantStatusRepository"/>.
    /// PartitionKey = tenantId (lowercased), RowKey = "status".
    /// </summary>
    public class TableSlaTenantStatusRepository : ISlaTenantStatusRepository
    {
        private readonly TableClient _table;
        private readonly ILogger<TableSlaTenantStatusRepository> _logger;

        public TableSlaTenantStatusRepository(
            TableStorageService storage,
            ILogger<TableSlaTenantStatusRepository> logger)
        {
            _logger = logger;
            _table = storage.GetTableClient(Constants.TableNames.SlaTenantStatus);
        }

        public async Task<SlaTenantStatus?> GetAsync(string tenantId)
        {
            var (status, _) = await GetWithETagAsync(tenantId);
            return status;
        }

        public async Task<(SlaTenantStatus? Status, string? ETag)> GetWithETagAsync(string tenantId)
        {
            if (string.IsNullOrWhiteSpace(tenantId)) return (null, null);

            try
            {
                var partitionKey = tenantId.ToLowerInvariant();
                var entity = await _table.GetEntityAsync<TableEntity>(partitionKey, SlaTenantStatus.StatusRowKey);
                return (MapFromEntity(entity.Value, tenantId), entity.Value.ETag.ToString());
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return (null, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load SLA status for tenant {TenantId}", tenantId);
                throw;
            }
        }

        public async Task<bool> UpsertAsync(SlaTenantStatus status)
        {
            if (status == null || string.IsNullOrWhiteSpace(status.TenantId))
                return false;

            try
            {
                var entity = MapToEntity(status);
                await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upsert SLA status for tenant {TenantId}", status.TenantId);
                return false;
            }
        }

        public async Task<bool> TryUpsertAsync(SlaTenantStatus status, string? ifMatchETag)
        {
            if (status == null || string.IsNullOrWhiteSpace(status.TenantId))
                return false;

            try
            {
                var entity = MapToEntity(status);

                if (ifMatchETag is null)
                {
                    // Caller observed no row — try to insert. A pre-existing row means another
                    // writer beat us; surface that as a conflict so the caller refetches.
                    await _table.AddEntityAsync(entity);
                    return true;
                }

                await _table.UpdateEntityAsync(entity, new ETag(ifMatchETag), TableUpdateMode.Replace);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 409 || ex.Status == 412)
            {
                // 409 EntityAlreadyExists (AddEntity) or 412 PreconditionFailed (UpdateEntity ETag mismatch)
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed conditional upsert of SLA status for tenant {TenantId}", status.TenantId);
                return false;
            }
        }

        public async Task<List<SlaTenantStatus>> ListAllActiveAsync()
        {
            var all = await ListAllAsync();
            return all.Where(s => s.IsAnyTypeActive()).ToList();
        }

        public async Task<List<SlaTenantStatus>> ListAllAsync()
        {
            var results = new List<SlaTenantStatus>();
            try
            {
                await foreach (var entity in _table.QueryAsync<TableEntity>(
                    filter: $"RowKey eq '{SlaTenantStatus.StatusRowKey}'"))
                {
                    results.Add(MapFromEntity(entity, entity.PartitionKey));
                }
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogWarning("SlaTenantStatus table not found — returning empty list");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to list SLA status rows");
            }
            return results;
        }

        // ── Mapping ───────────────────────────────────────────────────────────

        internal static TableEntity MapToEntity(SlaTenantStatus s)
        {
            var partitionKey = s.TenantId.ToLowerInvariant();
            var entity = new TableEntity(partitionKey, SlaTenantStatus.StatusRowKey)
            {
                { "LastEvaluatedAt", s.LastEvaluatedAt },

                // SuccessRate
                { "SuccessRate_IsActive", s.SuccessRate_IsActive },
                { "SuccessRate_CurrentValue", s.SuccessRate_CurrentValue },
                { "SuccessRate_TargetValue", s.SuccessRate_TargetValue },
                { "SuccessRate_ThresholdValue", s.SuccessRate_ThresholdValue },
                { "SuccessRate_TotalSessions", s.SuccessRate_TotalSessions },
                { "SuccessRate_FailedSessions", s.SuccessRate_FailedSessions },
                { "SuccessRate_FirstBreachAt", s.SuccessRate_FirstBreachAt },
                { "SuccessRate_LastBreachAt", s.SuccessRate_LastBreachAt },
                { "SuccessRate_LastNotifiedAt", s.SuccessRate_LastNotifiedAt },
                { "SuccessRate_ResolvedAt", s.SuccessRate_ResolvedAt },

                // Duration
                { "Duration_IsActive", s.Duration_IsActive },
                { "Duration_CurrentP95Minutes", s.Duration_CurrentP95Minutes },
                { "Duration_TargetMinutes", s.Duration_TargetMinutes },
                { "Duration_TotalSessions", s.Duration_TotalSessions },
                { "Duration_FirstBreachAt", s.Duration_FirstBreachAt },
                { "Duration_LastBreachAt", s.Duration_LastBreachAt },
                { "Duration_LastNotifiedAt", s.Duration_LastNotifiedAt },
                { "Duration_ResolvedAt", s.Duration_ResolvedAt },

                // AppInstall
                { "AppInstall_IsActive", s.AppInstall_IsActive },
                { "AppInstall_CurrentRate", s.AppInstall_CurrentRate },
                { "AppInstall_TargetRate", s.AppInstall_TargetRate },
                { "AppInstall_TopFailingApp", s.AppInstall_TopFailingApp },
                { "AppInstall_FirstBreachAt", s.AppInstall_FirstBreachAt },
                { "AppInstall_LastBreachAt", s.AppInstall_LastBreachAt },
                { "AppInstall_LastNotifiedAt", s.AppInstall_LastNotifiedAt },
                { "AppInstall_ResolvedAt", s.AppInstall_ResolvedAt },

                // ConsecutiveFailures
                { "ConsecutiveFailures_IsActive", s.ConsecutiveFailures_IsActive },
                { "ConsecutiveFailures_Count", s.ConsecutiveFailures_Count },
                { "ConsecutiveFailures_LastDevice", s.ConsecutiveFailures_LastDevice },
                { "ConsecutiveFailures_LastReason", s.ConsecutiveFailures_LastReason },
                { "ConsecutiveFailures_FirstAt", s.ConsecutiveFailures_FirstAt },
                { "ConsecutiveFailures_LastNotifiedAt", s.ConsecutiveFailures_LastNotifiedAt },
                { "ConsecutiveFailures_ResolvedAt", s.ConsecutiveFailures_ResolvedAt },
            };
            return entity;
        }

        internal static SlaTenantStatus MapFromEntity(TableEntity e, string tenantId)
        {
            return new SlaTenantStatus
            {
                TenantId = tenantId,
                LastEvaluatedAt = e.GetDateTime("LastEvaluatedAt") ?? DateTime.MinValue,

                SuccessRate_IsActive = e.GetBoolean("SuccessRate_IsActive") ?? false,
                SuccessRate_CurrentValue = e.GetDouble("SuccessRate_CurrentValue"),
                SuccessRate_TargetValue = e.GetDouble("SuccessRate_TargetValue"),
                SuccessRate_ThresholdValue = e.GetDouble("SuccessRate_ThresholdValue"),
                SuccessRate_TotalSessions = e.GetInt32("SuccessRate_TotalSessions"),
                SuccessRate_FailedSessions = e.GetInt32("SuccessRate_FailedSessions"),
                SuccessRate_FirstBreachAt = e.GetDateTime("SuccessRate_FirstBreachAt"),
                SuccessRate_LastBreachAt = e.GetDateTime("SuccessRate_LastBreachAt"),
                SuccessRate_LastNotifiedAt = e.GetDateTime("SuccessRate_LastNotifiedAt"),
                SuccessRate_ResolvedAt = e.GetDateTime("SuccessRate_ResolvedAt"),

                Duration_IsActive = e.GetBoolean("Duration_IsActive") ?? false,
                Duration_CurrentP95Minutes = e.GetDouble("Duration_CurrentP95Minutes"),
                Duration_TargetMinutes = e.GetInt32("Duration_TargetMinutes"),
                Duration_TotalSessions = e.GetInt32("Duration_TotalSessions"),
                Duration_FirstBreachAt = e.GetDateTime("Duration_FirstBreachAt"),
                Duration_LastBreachAt = e.GetDateTime("Duration_LastBreachAt"),
                Duration_LastNotifiedAt = e.GetDateTime("Duration_LastNotifiedAt"),
                Duration_ResolvedAt = e.GetDateTime("Duration_ResolvedAt"),

                AppInstall_IsActive = e.GetBoolean("AppInstall_IsActive") ?? false,
                AppInstall_CurrentRate = e.GetDouble("AppInstall_CurrentRate"),
                AppInstall_TargetRate = e.GetDouble("AppInstall_TargetRate"),
                AppInstall_TopFailingApp = e.GetString("AppInstall_TopFailingApp"),
                AppInstall_FirstBreachAt = e.GetDateTime("AppInstall_FirstBreachAt"),
                AppInstall_LastBreachAt = e.GetDateTime("AppInstall_LastBreachAt"),
                AppInstall_LastNotifiedAt = e.GetDateTime("AppInstall_LastNotifiedAt"),
                AppInstall_ResolvedAt = e.GetDateTime("AppInstall_ResolvedAt"),

                ConsecutiveFailures_IsActive = e.GetBoolean("ConsecutiveFailures_IsActive") ?? false,
                ConsecutiveFailures_Count = e.GetInt32("ConsecutiveFailures_Count"),
                ConsecutiveFailures_LastDevice = e.GetString("ConsecutiveFailures_LastDevice"),
                ConsecutiveFailures_LastReason = e.GetString("ConsecutiveFailures_LastReason"),
                ConsecutiveFailures_FirstAt = e.GetDateTime("ConsecutiveFailures_FirstAt"),
                ConsecutiveFailures_LastNotifiedAt = e.GetDateTime("ConsecutiveFailures_LastNotifiedAt"),
                ConsecutiveFailures_ResolvedAt = e.GetDateTime("ConsecutiveFailures_ResolvedAt"),
            };
        }
    }
}
