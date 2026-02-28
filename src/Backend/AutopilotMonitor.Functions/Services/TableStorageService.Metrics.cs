using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    public partial class TableStorageService
    {
        // ===== HISTORICAL METRICS METHODS =====

        /// <summary>
        /// Saves a historical metrics snapshot
        /// </summary>
        public async Task<bool> SaveUsageMetricsSnapshotAsync(UsageMetricsSnapshot metrics)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.UsageMetrics);

                var entity = new TableEntity(metrics.Date, metrics.TenantId)
                {
                    ["ComputedAt"] = metrics.ComputedAt,
                    ["ComputeDurationMs"] = metrics.ComputeDurationMs,
                    ["SessionsTotal"] = metrics.SessionsTotal,
                    ["SessionsSucceeded"] = metrics.SessionsSucceeded,
                    ["SessionsFailed"] = metrics.SessionsFailed,
                    ["SessionsInProgress"] = metrics.SessionsInProgress,
                    ["SessionsSuccessRate"] = metrics.SessionsSuccessRate,
                    ["AvgDurationMinutes"] = metrics.AvgDurationMinutes,
                    ["MedianDurationMinutes"] = metrics.MedianDurationMinutes,
                    ["P95DurationMinutes"] = metrics.P95DurationMinutes,
                    ["P99DurationMinutes"] = metrics.P99DurationMinutes,
                    ["UniqueTenants"] = metrics.UniqueTenants,
                    ["UniqueUsers"] = metrics.UniqueUsers,
                    ["LoginCount"] = metrics.LoginCount,
                    ["TopManufacturers"] = metrics.TopManufacturers,
                    ["TopModels"] = metrics.TopModels
                };

                await tableClient.UpsertEntityAsync(entity);
                _logger.LogInformation($"Saved historical metrics for {metrics.Date} / {metrics.TenantId}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to save historical metrics for {metrics.Date} / {metrics.TenantId}");
                return false;
            }
        }

        /// <summary>
        /// Gets historical metrics for a date range
        /// </summary>
        public async Task<List<UsageMetricsSnapshot>> GetUsageMetricsSnapshotAsync(string? tenantId = null, string? startDate = null, string? endDate = null, int maxResults = 100)
        {
            if (!string.IsNullOrEmpty(tenantId))
                SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.UsageMetrics);

                // Build filter
                var filters = new List<string>();

                if (!string.IsNullOrEmpty(startDate))
                    filters.Add($"PartitionKey ge '{startDate}'");

                if (!string.IsNullOrEmpty(endDate))
                    filters.Add($"PartitionKey le '{endDate}'");

                if (!string.IsNullOrEmpty(tenantId))
                    filters.Add($"RowKey eq '{tenantId}'");

                var filter = filters.Count > 0 ? string.Join(" and ", filters) : null;
                var query = tableClient.QueryAsync<TableEntity>(filter: filter);

                var results = new List<UsageMetricsSnapshot>();
                await foreach (var entity in query)
                {
                    results.Add(new UsageMetricsSnapshot
                    {
                        Date = entity.PartitionKey,
                        TenantId = entity.RowKey,
                        ComputedAt = entity.GetDateTimeOffset("ComputedAt")?.UtcDateTime ?? DateTime.UtcNow,
                        ComputeDurationMs = entity.GetInt32("ComputeDurationMs") ?? 0,
                        SessionsTotal = entity.GetInt32("SessionsTotal") ?? 0,
                        SessionsSucceeded = entity.GetInt32("SessionsSucceeded") ?? 0,
                        SessionsFailed = entity.GetInt32("SessionsFailed") ?? 0,
                        SessionsInProgress = entity.GetInt32("SessionsInProgress") ?? 0,
                        SessionsSuccessRate = entity.GetDouble("SessionsSuccessRate") ?? 0,
                        AvgDurationMinutes = entity.GetDouble("AvgDurationMinutes") ?? 0,
                        MedianDurationMinutes = entity.GetDouble("MedianDurationMinutes") ?? 0,
                        P95DurationMinutes = entity.GetDouble("P95DurationMinutes") ?? 0,
                        P99DurationMinutes = entity.GetDouble("P99DurationMinutes") ?? 0,
                        UniqueTenants = entity.GetInt32("UniqueTenants") ?? 0,
                        UniqueUsers = entity.GetInt32("UniqueUsers") ?? 0,
                        LoginCount = entity.GetInt32("LoginCount") ?? 0,
                        TopManufacturers = entity.GetString("TopManufacturers") ?? "[]",
                        TopModels = entity.GetString("TopModels") ?? "[]"
                    });

                    if (results.Count >= maxResults) break;
                }

                return results.OrderByDescending(m => m.Date).Take(maxResults).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get historical metrics");
                return new List<UsageMetricsSnapshot>();
            }
        }

        /// <summary>
        /// Checks if a global usage metrics snapshot exists for a given date.
        /// Used by maintenance catch-up to determine which dates need aggregation.
        /// </summary>
        public async Task<bool> HasUsageMetricsSnapshotAsync(string date)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.UsageMetrics);
                await tableClient.GetEntityAsync<TableEntity>(date, "global");
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to check usage metrics snapshot for {date}");
                return false;
            }
        }

        // ===== APP INSTALL SUMMARIES METHODS =====

        /// <summary>
        /// Stores or updates an app install summary.
        /// Merges with any existing record so StartedAt is never overwritten with a later timestamp.
        /// PartitionKey: TenantId, RowKey: {SessionId}_{AppName}
        /// </summary>
        public async Task<bool> StoreAppInstallSummaryAsync(AppInstallSummary summary)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.AppInstallSummaries);
                var rowKey = $"{summary.SessionId}_{summary.AppName}";

                // Merge with existing record to preserve StartedAt from a prior batch
                try
                {
                    var existing = await tableClient.GetEntityAsync<TableEntity>(summary.TenantId, rowKey);
                    var existingStartedAt = existing.Value.GetDateTimeOffset("StartedAt")?.UtcDateTime;
                    if (existingStartedAt.HasValue && existingStartedAt.Value != DateTime.MinValue)
                    {
                        // Keep the earlier StartedAt; recalculate duration if CompletedAt is now known
                        if (summary.StartedAt == DateTime.MinValue || existingStartedAt.Value < summary.StartedAt)
                        {
                            summary.StartedAt = existingStartedAt.Value;
                            if (summary.CompletedAt.HasValue && summary.CompletedAt.Value >= summary.StartedAt)
                            {
                                summary.DurationSeconds = (int)(summary.CompletedAt.Value - summary.StartedAt).TotalSeconds;
                            }
                        }
                    }
                }
                catch (Azure.RequestFailedException ex) when (ex.Status == 404)
                {
                    // No existing record – nothing to merge
                }

                var entity = new TableEntity(summary.TenantId, rowKey)
                {
                    ["AppName"] = summary.AppName ?? string.Empty,
                    ["SessionId"] = summary.SessionId ?? string.Empty,
                    ["TenantId"] = summary.TenantId ?? string.Empty,
                    ["Status"] = summary.Status ?? "InProgress",
                    ["DurationSeconds"] = summary.DurationSeconds,
                    ["DownloadBytes"] = summary.DownloadBytes,
                    ["DownloadDurationSeconds"] = summary.DownloadDurationSeconds,
                    ["FailureCode"] = summary.FailureCode ?? string.Empty,
                    ["FailureMessage"] = summary.FailureMessage ?? string.Empty,
                    ["StartedAt"] = summary.StartedAt,
                    ["CompletedAt"] = summary.CompletedAt
                };

                await tableClient.UpsertEntityAsync(entity);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to store app install summary for {summary.AppName}");
                return false;
            }
        }

        /// <summary>
        /// Gets all app install summaries for a tenant (fleet-level metrics)
        /// </summary>
        public async Task<List<AppInstallSummary>> GetAppInstallSummariesByTenantAsync(string tenantId)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.AppInstallSummaries);
                var query = tableClient.QueryAsync<TableEntity>(filter: $"PartitionKey eq '{tenantId}'");

                var summaries = new List<AppInstallSummary>();
                await foreach (var entity in query)
                {
                    summaries.Add(MapToAppInstallSummary(entity));
                }

                return summaries;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get app install summaries for tenant {tenantId}");
                return new List<AppInstallSummary>();
            }
        }

        /// <summary>
        /// Gets all app install summaries across all tenants (for galactic admin mode)
        /// </summary>
        public async Task<List<AppInstallSummary>> GetAllAppInstallSummariesAsync()
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.AppInstallSummaries);
                var query = tableClient.QueryAsync<TableEntity>();

                var summaries = new List<AppInstallSummary>();
                await foreach (var entity in query)
                {
                    summaries.Add(MapToAppInstallSummary(entity));
                }

                return summaries;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get all app install summaries");
                return new List<AppInstallSummary>();
            }
        }

        private AppInstallSummary MapToAppInstallSummary(TableEntity entity)
        {
            return new AppInstallSummary
            {
                AppName = entity.GetString("AppName") ?? string.Empty,
                SessionId = entity.GetString("SessionId") ?? string.Empty,
                TenantId = entity.GetString("TenantId") ?? entity.PartitionKey,
                Status = entity.GetString("Status") ?? "InProgress",
                DurationSeconds = entity.GetInt32("DurationSeconds") ?? 0,
                DownloadBytes = entity.GetInt64("DownloadBytes") ?? 0,
                DownloadDurationSeconds = entity.GetInt32("DownloadDurationSeconds") ?? 0,
                FailureCode = entity.GetString("FailureCode") ?? string.Empty,
                FailureMessage = entity.GetString("FailureMessage") ?? string.Empty,
                StartedAt = entity.GetDateTimeOffset("StartedAt")?.UtcDateTime ?? DateTime.MinValue,
                CompletedAt = entity.GetDateTimeOffset("CompletedAt")?.UtcDateTime
            };
        }

        // ===== PLATFORM STATS METHODS =====

        /// <summary>
        /// Gets the current platform stats (single row: global/current)
        /// </summary>
        public async Task<PlatformStats?> GetPlatformStatsAsync()
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.PlatformStats);
                var response = await tableClient.GetEntityAsync<TableEntity>("global", "current");
                var entity = response.Value;

                return new PlatformStats
                {
                    TotalEnrollments = entity.GetInt64("TotalEnrollments") ?? 0,
                    TotalUsers = entity.GetInt64("TotalUsers") ?? 0,
                    TotalTenants = entity.GetInt64("TotalTenants") ?? 0,
                    TotalSignedUpTenants = entity.GetInt64("TotalSignedUpTenants") ?? 0,
                    UniqueDeviceModels = entity.GetInt64("UniqueDeviceModels") ?? 0,
                    TotalEventsProcessed = entity.GetInt64("TotalEventsProcessed") ?? 0,
                    SuccessfulEnrollments = entity.GetInt64("SuccessfulEnrollments") ?? 0,
                    IssuesDetected = entity.GetInt64("IssuesDetected") ?? 0,
                    LastFullCompute = entity.GetDateTimeOffset("LastFullCompute")?.UtcDateTime ?? DateTime.MinValue,
                    LastUpdated = entity.GetDateTimeOffset("LastUpdated")?.UtcDateTime ?? DateTime.MinValue
                };
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get platform stats");
                return null;
            }
        }

        /// <summary>
        /// Saves the full platform stats (upsert)
        /// </summary>
        public async Task<bool> SavePlatformStatsAsync(PlatformStats stats)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.PlatformStats);

                var entity = new TableEntity("global", "current")
                {
                    ["TotalEnrollments"] = stats.TotalEnrollments,
                    ["TotalUsers"] = stats.TotalUsers,
                    ["TotalTenants"] = stats.TotalTenants,
                    ["TotalSignedUpTenants"] = stats.TotalSignedUpTenants,
                    ["UniqueDeviceModels"] = stats.UniqueDeviceModels,
                    ["TotalEventsProcessed"] = stats.TotalEventsProcessed,
                    ["SuccessfulEnrollments"] = stats.SuccessfulEnrollments,
                    ["IssuesDetected"] = stats.IssuesDetected,
                    ["LastFullCompute"] = stats.LastFullCompute,
                    ["LastUpdated"] = stats.LastUpdated
                };

                await tableClient.UpsertEntityAsync(entity);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save platform stats");
                return false;
            }
        }

        /// <summary>
        /// Increments a specific platform stat counter atomically.
        /// Reads current value, increments, and writes back.
        /// </summary>
        public async Task IncrementPlatformStatAsync(string field, long amount = 1)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.PlatformStats);

                TableEntity entity;

                try
                {
                    var response = await tableClient.GetEntityAsync<TableEntity>("global", "current");
                    entity = response.Value;
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    entity = new TableEntity("global", "current")
                    {
                        ["TotalEnrollments"] = 0L,
                        ["TotalUsers"] = 0L,
                        ["TotalTenants"] = 0L,
                        ["TotalSignedUpTenants"] = 0L,
                        ["UniqueDeviceModels"] = 0L,
                        ["TotalEventsProcessed"] = 0L,
                        ["SuccessfulEnrollments"] = 0L,
                        ["IssuesDetected"] = 0L,
                        ["LastFullCompute"] = DateTime.MinValue,
                        ["LastUpdated"] = DateTime.UtcNow
                    };
                }

                var current = entity.GetInt64(field) ?? 0;
                entity[field] = current + amount;
                entity["LastUpdated"] = DateTime.UtcNow;

                await tableClient.UpsertEntityAsync(entity);
            }
            catch (Exception ex)
            {
                // Non-fatal: don't break the caller if stats update fails
                _logger.LogWarning(ex, $"Failed to increment platform stat {field}");
            }
        }

        // ===== USER ACTIVITY METHODS =====

        /// <summary>
        /// Records a user login activity
        /// PartitionKey: TenantId, RowKey: {invertedTicks}_{Guid} for reverse-chronological ordering
        /// </summary>
        public async Task RecordUserLoginAsync(string tenantId, string upn, string? displayName, string? objectId)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.UserActivity);
                var now = DateTime.UtcNow;
                var invertedTicks = (DateTime.MaxValue.Ticks - now.Ticks).ToString("D20");

                var entity = new TableEntity(tenantId, $"{invertedTicks}_{Guid.NewGuid():N}")
                {
                    ["Upn"] = upn ?? string.Empty,
                    ["DisplayName"] = displayName ?? string.Empty,
                    ["ObjectId"] = objectId ?? string.Empty,
                    ["LoginAt"] = now
                };

                await tableClient.AddEntityAsync(entity);
                _logger.LogDebug($"Recorded login for {upn} in tenant {tenantId}");
            }
            catch (Exception ex)
            {
                // Don't fail the login if activity recording fails
                _logger.LogWarning(ex, $"Failed to record login activity for {upn}");
            }
        }

        /// <summary>
        /// Gets user activity metrics for a specific tenant
        /// </summary>
        public async Task<UserActivityMetrics> GetUserActivityMetricsAsync(string tenantId)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.UserActivity);
                var query = tableClient.QueryAsync<TableEntity>(filter: $"PartitionKey eq '{tenantId}'");

                var now = DateTime.UtcNow;
                var today = now.Date;
                var last7Days = now.AddDays(-7);
                var last30Days = now.AddDays(-30);

                var allUpns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var todayUpns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var last7Upns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var last30Upns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int todayLogins = 0;

                await foreach (var entity in query)
                {
                    var upn = entity.GetString("Upn") ?? string.Empty;
                    var loginAt = entity.GetDateTime("LoginAt") ?? DateTime.MinValue;

                    if (string.IsNullOrEmpty(upn)) continue;

                    allUpns.Add(upn);

                    if (loginAt >= last30Days) last30Upns.Add(upn);
                    if (loginAt >= last7Days) last7Upns.Add(upn);
                    if (loginAt >= today)
                    {
                        todayUpns.Add(upn);
                        todayLogins++;
                    }
                }

                return new UserActivityMetrics
                {
                    TotalUniqueUsers = allUpns.Count,
                    DailyLogins = todayLogins,
                    ActiveUsersLast7Days = last7Upns.Count,
                    ActiveUsersLast30Days = last30Upns.Count
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get user activity metrics for tenant {tenantId}");
                return new UserActivityMetrics();
            }
        }

        /// <summary>
        /// Gets user activity metrics across all tenants (for galactic admin)
        /// </summary>
        public async Task<UserActivityMetrics> GetAllUserActivityMetricsAsync()
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.UserActivity);
                var query = tableClient.QueryAsync<TableEntity>();

                var now = DateTime.UtcNow;
                var today = now.Date;
                var last7Days = now.AddDays(-7);
                var last30Days = now.AddDays(-30);

                var allUpns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var todayUpns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var last7Upns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var last30Upns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int todayLogins = 0;

                await foreach (var entity in query)
                {
                    var upn = entity.GetString("Upn") ?? string.Empty;
                    var loginAt = entity.GetDateTime("LoginAt") ?? DateTime.MinValue;

                    if (string.IsNullOrEmpty(upn)) continue;

                    allUpns.Add(upn);

                    if (loginAt >= last30Days) last30Upns.Add(upn);
                    if (loginAt >= last7Days) last7Upns.Add(upn);
                    if (loginAt >= today)
                    {
                        todayUpns.Add(upn);
                        todayLogins++;
                    }
                }

                return new UserActivityMetrics
                {
                    TotalUniqueUsers = allUpns.Count,
                    DailyLogins = todayLogins,
                    ActiveUsersLast7Days = last7Upns.Count,
                    ActiveUsersLast30Days = last30Upns.Count
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get all user activity metrics");
                return new UserActivityMetrics();
            }
        }

        /// <summary>
        /// Gets user login count for a specific date range (used by daily maintenance)
        /// </summary>
        public async Task<(int uniqueUsers, int loginCount)> GetUserActivityForDateAsync(string? tenantId, DateTime date)
        {
            if (!string.IsNullOrEmpty(tenantId) && tenantId != "global")
                SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.UserActivity);
                var startOfDay = date.Date;
                var endOfDay = startOfDay.AddDays(1);

                string filter;
                if (!string.IsNullOrEmpty(tenantId) && tenantId != "global")
                {
                    filter = $"PartitionKey eq '{tenantId}' and LoginAt ge datetime'{startOfDay:yyyy-MM-ddTHH:mm:ss}Z' and LoginAt lt datetime'{endOfDay:yyyy-MM-ddTHH:mm:ss}Z'";
                }
                else
                {
                    filter = $"LoginAt ge datetime'{startOfDay:yyyy-MM-ddTHH:mm:ss}Z' and LoginAt lt datetime'{endOfDay:yyyy-MM-ddTHH:mm:ss}Z'";
                }

                var query = tableClient.QueryAsync<TableEntity>(filter: filter);

                var upns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int loginCount = 0;

                await foreach (var entity in query)
                {
                    var upn = entity.GetString("Upn") ?? string.Empty;
                    if (!string.IsNullOrEmpty(upn))
                    {
                        upns.Add(upn);
                        loginCount++;
                    }
                }

                return (upns.Count, loginCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get user activity for date {date:yyyy-MM-dd}");
                return (0, 0);
            }
        }
    }
}
