using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Table Storage implementation of <see cref="IHardwareRejectionNotificationTracker"/>.
    /// PartitionKey = tenantId (lowercased), RowKey = "{manufacturer-lower}|{model-lower}" (trimmed).
    /// Race-safe via AddEntityAsync: Azure Table Storage returns 409 Conflict if the entity already exists.
    /// </summary>
    public class TableHardwareRejectionNotificationTracker : IHardwareRejectionNotificationTracker
    {
        private readonly TableClient _table;
        private readonly ILogger<TableHardwareRejectionNotificationTracker> _logger;

        public TableHardwareRejectionNotificationTracker(
            TableStorageService storage,
            ILogger<TableHardwareRejectionNotificationTracker> logger)
        {
            _logger = logger;
            _table = storage.GetTableClient(Constants.TableNames.HardwareRejectionNotificationTracker);
        }

        public async Task<bool> TryRegisterFirstNotificationAsync(string tenantId, string manufacturer, string model)
        {
            if (string.IsNullOrWhiteSpace(tenantId)
                || string.IsNullOrWhiteSpace(manufacturer)
                || string.IsNullOrWhiteSpace(model))
            {
                return false;
            }

            var partitionKey = tenantId.ToLowerInvariant();
            var rowKey = BuildRowKey(manufacturer, model);

            var entity = new TableEntity(partitionKey, rowKey)
            {
                ["TenantId"] = tenantId,
                ["Manufacturer"] = manufacturer,
                ["Model"] = model,
                ["FirstNotifiedAt"] = DateTime.UtcNow
            };

            try
            {
                await _table.AddEntityAsync(entity);
                _logger.LogInformation(
                    "HardwareRejection tracker registered: tenant={TenantId} mfr={Manufacturer} model={Model}",
                    tenantId, manufacturer, model);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 409)
            {
                // Already notified for this (tenant, manufacturer, model) — no second bell.
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "HardwareRejection tracker failed: tenant={TenantId} mfr={Manufacturer} model={Model}",
                    tenantId, manufacturer, model);
                // On unexpected failure, return false so we do not double-fire if the row was actually written.
                return false;
            }
        }

        public async Task<int> DeleteOlderThanAsync(DateTime cutoffUtc)
        {
            try
            {
                // Rows are insert-once (AddEntityAsync, never updated), so FirstNotifiedAt == creation time.
                // Pruning here resets the lifetime dedup for a given model: if the same model is rejected
                // again after the cutoff, the bell fires once more — acceptable, since the portal only
                // surfaces recent rejections anyway.
                var filter = $"FirstNotifiedAt lt datetime'{cutoffUtc:yyyy-MM-ddTHH:mm:ss}Z'";
                var query = _table.QueryAsync<TableEntity>(filter: filter, select: new[] { "PartitionKey", "RowKey" });

                int deleted = 0;
                await foreach (var entity in query)
                {
                    try
                    {
                        await _table.DeleteEntityAsync(entity.PartitionKey, entity.RowKey);
                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete hardware-rejection tracker row {PK}/{RK}", entity.PartitionKey, entity.RowKey);
                    }
                }

                if (deleted > 0)
                    _logger.LogInformation("Deleted {Count} hardware-rejection tracker rows older than {Cutoff:yyyy-MM-dd}", deleted, cutoffUtc);

                return deleted;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete old hardware-rejection tracker rows");
                return 0;
            }
        }

        internal static string BuildRowKey(string manufacturer, string model)
        {
            var mfr = (manufacturer ?? string.Empty).Trim().ToLowerInvariant();
            var mdl = (model ?? string.Empty).Trim().ToLowerInvariant();
            return $"{mfr}|{mdl}";
        }
    }
}
