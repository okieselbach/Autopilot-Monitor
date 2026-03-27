using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Table Storage implementation of INotificationRepository.
    /// Manages GlobalNotifications and SessionReports tables.
    /// Uses inverted-tick RowKeys to sort newest-first.
    /// </summary>
    public class TableNotificationRepository : INotificationRepository
    {
        private readonly TableClient _notificationsTableClient;
        private readonly TableClient _reportsTableClient;
        private readonly ILogger<TableNotificationRepository> _logger;

        public TableNotificationRepository(
            IConfiguration configuration,
            ILogger<TableNotificationRepository> logger)
        {
            _logger = logger;

            var connectionString = configuration["AzureTableStorageConnectionString"];
            var serviceClient = new TableServiceClient(connectionString);
            _notificationsTableClient = serviceClient.GetTableClient(Constants.TableNames.GlobalNotifications);
            _reportsTableClient = serviceClient.GetTableClient(Constants.TableNames.SessionReports);
        }

        // --- Global Notifications ---

        public async Task<bool> AddNotificationAsync(GlobalNotification notification)
        {
            try
            {
                var invertedTicks = (DateTime.MaxValue.Ticks - notification.CreatedAt.Ticks).ToString("D19");
                var notificationId = notification.NotificationId;
                if (string.IsNullOrEmpty(notificationId))
                    notificationId = Guid.NewGuid().ToString("N")[..12];

                var entity = new TableEntity("notifications", $"{invertedTicks}_{notificationId}")
                {
                    ["NotificationId"] = notificationId,
                    ["Type"] = notification.Type ?? "info",
                    ["Title"] = notification.Title ?? string.Empty,
                    ["Message"] = notification.Message ?? string.Empty,
                    ["Href"] = notification.Href,
                    ["CreatedAt"] = notification.CreatedAt,
                    ["CreatedBy"] = notification.CreatedBy ?? string.Empty,
                    ["Dismissed"] = notification.IsDismissed
                };

                await _notificationsTableClient.UpsertEntityAsync(entity);
                _logger.LogInformation("Global notification stored: {NotificationId} ({Type})", notificationId, notification.Type);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to store global notification ({Type}: {Title})", notification.Type, notification.Title);
                return false;
            }
        }

        public async Task<List<GlobalNotification>> GetNotificationsAsync(int maxResults = 50)
        {
            try
            {
                var notifications = new List<GlobalNotification>();
                var count = 0;

                await foreach (var entity in _notificationsTableClient.QueryAsync<TableEntity>(
                    filter: $"PartitionKey eq 'notifications'"))
                {
                    var dismissed = entity.GetBoolean("Dismissed") ?? false;
                    if (dismissed) continue;

                    notifications.Add(MapToGlobalNotification(entity));
                    count++;
                    if (count >= maxResults) break;
                }

                return notifications;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogWarning("GlobalNotifications table not found — returning empty list");
                return new List<GlobalNotification>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve global notifications");
                return new List<GlobalNotification>();
            }
        }

        public async Task<bool> DismissNotificationAsync(string notificationId, string dismissedBy)
        {
            try
            {
                await foreach (var entity in _notificationsTableClient.QueryAsync<TableEntity>(
                    filter: $"PartitionKey eq 'notifications'"))
                {
                    if (entity.GetString("NotificationId") == notificationId)
                    {
                        entity["Dismissed"] = true;
                        entity["DismissedBy"] = dismissedBy;
                        entity["DismissedAt"] = DateTime.UtcNow;
                        await _notificationsTableClient.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace);
                        _logger.LogInformation("Global notification dismissed: {NotificationId}", notificationId);
                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to dismiss global notification {NotificationId}", notificationId);
                return false;
            }
        }

        public async Task<int> DismissAllNotificationsAsync()
        {
            try
            {
                var count = 0;

                await foreach (var entity in _notificationsTableClient.QueryAsync<TableEntity>(
                    filter: $"PartitionKey eq 'notifications'"))
                {
                    var dismissed = entity.GetBoolean("Dismissed") ?? false;
                    if (dismissed) continue;

                    entity["Dismissed"] = true;
                    entity["DismissedAt"] = DateTime.UtcNow;
                    await _notificationsTableClient.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace);
                    count++;
                }

                _logger.LogInformation("Dismissed {Count} global notifications", count);
                return count;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to dismiss all global notifications");
                return 0;
            }
        }

        // --- Session Reports ---

        public async Task<bool> StoreSessionReportMetadataAsync(SessionReportMetadata metadata)
        {
            try
            {
                var invertedTicks = (DateTime.MaxValue.Ticks - metadata.SubmittedAt.Ticks).ToString("D19");
                var entity = new TableEntity("reports", $"{invertedTicks}_{metadata.ReportId}")
                {
                    ["ReportId"] = metadata.ReportId ?? string.Empty,
                    ["TenantId"] = metadata.TenantId ?? string.Empty,
                    ["SessionId"] = metadata.SessionId ?? string.Empty,
                    ["Comment"] = metadata.Comment ?? string.Empty,
                    ["Email"] = metadata.Email ?? string.Empty,
                    ["BlobName"] = metadata.BlobName ?? string.Empty,
                    ["SubmittedBy"] = metadata.SubmittedBy ?? string.Empty,
                    ["SubmittedAt"] = metadata.SubmittedAt
                };

                await _reportsTableClient.UpsertEntityAsync(entity);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to store session report metadata {ReportId}", metadata.ReportId);
                return false;
            }
        }

        public async Task<List<SessionReportMetadata>> GetSessionReportsAsync(string? tenantId = null, int maxResults = 50)
        {
            var results = new List<SessionReportMetadata>();

            try
            {
                var count = 0;
                await foreach (var entity in _reportsTableClient.QueryAsync<TableEntity>(
                    filter: $"PartitionKey eq 'reports'"))
                {
                    var report = MapToSessionReportMetadata(entity);

                    // Filter by tenantId if specified
                    if (tenantId != null && report.TenantId != tenantId)
                        continue;

                    results.Add(report);
                    count++;
                    if (count >= maxResults) break;
                }
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogDebug("SessionReports table does not exist yet, returning empty list");
            }

            return results;
        }

        public async Task<SessionReportMetadata?> GetSessionReportAsync(string reportId)
        {
            try
            {
                await foreach (var entity in _reportsTableClient.QueryAsync<TableEntity>(
                    filter: $"PartitionKey eq 'reports'"))
                {
                    if (entity.GetString("ReportId") == reportId)
                    {
                        return MapToSessionReportMetadata(entity);
                    }
                }
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogDebug("SessionReports table does not exist yet");
            }

            return null;
        }

        public async Task<bool> UpdateSessionReportAdminNoteAsync(string reportId, string adminNote)
        {
            TableEntity? found = null;
            await foreach (var entity in _reportsTableClient.QueryAsync<TableEntity>(
                filter: $"PartitionKey eq 'reports'"))
            {
                if (entity.GetString("ReportId") == reportId)
                {
                    found = entity;
                    break;
                }
            }

            if (found == null)
            {
                _logger.LogWarning("UpdateAdminNote: report {ReportId} not found", reportId);
                return false;
            }

            found["AdminNote"] = adminNote ?? string.Empty;
            await _reportsTableClient.UpsertEntityAsync(found, TableUpdateMode.Merge);

            _logger.LogInformation("Updated AdminNote for report {ReportId}", reportId);
            return true;
        }

        // --- Helpers ---

        private static GlobalNotification MapToGlobalNotification(TableEntity entity)
        {
            return new GlobalNotification
            {
                NotificationId = entity.GetString("NotificationId") ?? string.Empty,
                Type = entity.GetString("Type") ?? "info",
                Title = entity.GetString("Title") ?? string.Empty,
                Message = entity.GetString("Message") ?? string.Empty,
                Href = entity.GetString("Href"),
                CreatedAt = entity.GetDateTimeOffset("CreatedAt")?.UtcDateTime ?? DateTime.MinValue,
                CreatedBy = entity.GetString("CreatedBy") ?? string.Empty,
                IsDismissed = entity.GetBoolean("Dismissed") ?? false,
                DismissedBy = entity.GetString("DismissedBy"),
                DismissedAt = entity.GetDateTimeOffset("DismissedAt")?.UtcDateTime
            };
        }

        private static SessionReportMetadata MapToSessionReportMetadata(TableEntity entity)
        {
            return new SessionReportMetadata
            {
                ReportId = entity.GetString("ReportId") ?? string.Empty,
                TenantId = entity.GetString("TenantId") ?? string.Empty,
                SessionId = entity.GetString("SessionId") ?? string.Empty,
                Comment = entity.GetString("Comment") ?? string.Empty,
                Email = entity.GetString("Email") ?? string.Empty,
                BlobName = entity.GetString("BlobName") ?? string.Empty,
                SubmittedBy = entity.GetString("SubmittedBy") ?? string.Empty,
                SubmittedAt = entity.GetDateTimeOffset("SubmittedAt")?.UtcDateTime ?? DateTime.MinValue,
                AdminNote = entity.GetString("AdminNote") ?? string.Empty
            };
        }
    }
}
