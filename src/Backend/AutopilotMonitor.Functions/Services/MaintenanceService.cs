using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Result of a maintenance operation
    /// </summary>
    public class MaintenanceResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public string TriggeredBy { get; set; } = string.Empty;
        public DateTime TriggeredAt { get; set; }
        public int DurationMs { get; set; }
        public bool StalledSessionsChecked { get; set; }
        public bool MetricsAggregated { get; set; }
        public string? AggregatedDate { get; set; }
        public bool DataCleanupExecuted { get; set; }
        public bool PlatformStatsRecomputed { get; set; }
    }

    /// <summary>
    /// Dedicated service for maintenance tasks:
    /// 1. Marks stalled sessions (InProgress for too long) as timed out
    /// 2. Aggregates metrics into historical snapshots
    /// 3. Deletes old sessions and events based on tenant retention policies
    /// 4. Recomputes platform-wide stats for the landing page
    /// </summary>
    public class MaintenanceService
    {
        private readonly TableStorageService _storageService;
        private readonly TenantConfigurationService _tenantConfigService;
        private readonly UsageMetricsService _usageMetricsService;
        private readonly ILogger<MaintenanceService> _logger;

        public MaintenanceService(
            TableStorageService storageService,
            TenantConfigurationService tenantConfigService,
            UsageMetricsService usageMetricsService,
            ILogger<MaintenanceService> logger)
        {
            _storageService = storageService;
            _tenantConfigService = tenantConfigService;
            _usageMetricsService = usageMetricsService;
            _logger = logger;
        }

        /// <summary>
        /// Runs all maintenance tasks (used by the daily timer trigger)
        /// </summary>
        public async Task RunAllAsync()
        {
            _logger.LogInformation($"Daily maintenance started at {DateTime.UtcNow}");
            var maintenanceStart = Stopwatch.StartNew();

            try
            {
                await MarkStalledSessionsAsTimedOutAsync();
                await AggregateYesterdayMetricsAsync();
                await CleanupOldDataAsync();
                await RecomputePlatformStatsAsync();

                maintenanceStart.Stop();
                _logger.LogInformation($"Daily maintenance completed in {maintenanceStart.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Daily maintenance failed");
            }
        }

        /// <summary>
        /// Manually triggered maintenance with flexible date selection
        /// </summary>
        public async Task<MaintenanceResult> RunManualAsync(DateTime? targetDate = null, bool aggregateOnly = false, string triggeredBy = "Unknown")
        {
            _logger.LogInformation($"Manual maintenance triggered by {triggeredBy} at {DateTime.UtcNow}");
            var maintenanceStart = Stopwatch.StartNew();
            var result = new MaintenanceResult { TriggeredBy = triggeredBy, TriggeredAt = DateTime.UtcNow };

            try
            {
                if (!aggregateOnly)
                {
                    await MarkStalledSessionsAsTimedOutAsync();
                    result.StalledSessionsChecked = true;
                }

                var dateToAggregate = targetDate ?? DateTime.UtcNow.AddDays(-1).Date;
                await AggregateMetricsForDateAsync(dateToAggregate);
                result.AggregatedDate = dateToAggregate.ToString("yyyy-MM-dd");
                result.MetricsAggregated = true;

                if (!aggregateOnly)
                {
                    await CleanupOldDataAsync();
                    result.DataCleanupExecuted = true;
                }

                await RecomputePlatformStatsAsync();
                result.PlatformStatsRecomputed = true;

                maintenanceStart.Stop();
                result.DurationMs = (int)maintenanceStart.ElapsedMilliseconds;
                result.Success = true;

                _logger.LogInformation($"Manual maintenance completed in {maintenanceStart.ElapsedMilliseconds}ms");
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Manual maintenance failed");
                result.Success = false;
                result.Error = ex.Message;
                maintenanceStart.Stop();
                result.DurationMs = (int)maintenanceStart.ElapsedMilliseconds;
                return result;
            }
        }

        /// <summary>
        /// Marks sessions that have been in "InProgress" status for too long as "Failed - Timed Out"
        /// </summary>
        private async Task MarkStalledSessionsAsTimedOutAsync()
        {
            _logger.LogInformation("Checking for stalled sessions...");
            var stalledStart = Stopwatch.StartNew();

            try
            {
                var tenantIds = await _storageService.GetAllTenantIdsAsync();
                int totalSessionsTimedOut = 0;

                foreach (var tenantId in tenantIds)
                {
                    try
                    {
                        var config = await _tenantConfigService.GetConfigurationAsync(tenantId);
                        var timeoutHours = config?.SessionTimeoutHours ?? 5;
                        var cutoffTime = DateTime.UtcNow.AddHours(-timeoutHours);

                        var sessions = await _storageService.GetSessionsAsync(tenantId, maxResults: 1000000);
                        var stalledSessions = sessions
                            .Where(s => s.Status == SessionStatus.InProgress && s.StartedAt < cutoffTime)
                            .ToList();

                        if (stalledSessions.Count == 0)
                        {
                            _logger.LogInformation($"Tenant {tenantId}: No stalled sessions found (timeout: {timeoutHours}h)");
                            continue;
                        }

                        int sessionCount = 0;

                        foreach (var session in stalledSessions)
                        {
                            await _storageService.UpdateSessionStatusAsync(
                                session.TenantId,
                                session.SessionId,
                                SessionStatus.Failed,
                                failureReason: $"Session timed out after {timeoutHours} hours (started at {session.StartedAt:yyyy-MM-dd HH:mm:ss} UTC)"
                            );
                            sessionCount++;
                        }

                        totalSessionsTimedOut += sessionCount;

                        await _storageService.LogAuditEntryAsync(
                            tenantId,
                            "SessionTimeout",
                            "Session",
                            $"{sessionCount} sessions",
                            "System.DailyMaintenance",
                            new Dictionary<string, string>
                            {
                                { "SessionsTimedOut", sessionCount.ToString() },
                                { "TimeoutHours", timeoutHours.ToString() },
                                { "CutoffTime", cutoffTime.ToString("yyyy-MM-dd HH:mm:ss") }
                            });

                        _logger.LogInformation($"Tenant {tenantId}: Marked {sessionCount} stalled sessions as timed out (timeout: {timeoutHours}h)");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to check stalled sessions for tenant {tenantId}");
                    }
                }

                stalledStart.Stop();
                _logger.LogInformation($"Stalled session check completed: {totalSessionsTimedOut} sessions timed out in {stalledStart.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check for stalled sessions");
            }
        }

        /// <summary>
        /// Aggregates yesterday's metrics and saves them as historical snapshots
        /// </summary>
        private async Task AggregateYesterdayMetricsAsync()
        {
            var yesterday = DateTime.UtcNow.AddDays(-1).Date;
            await AggregateMetricsForDateAsync(yesterday);
        }

        /// <summary>
        /// Aggregates metrics for a specific date and saves them as historical snapshots
        /// </summary>
        private async Task AggregateMetricsForDateAsync(DateTime targetDate)
        {
            _logger.LogInformation($"Aggregating metrics for {targetDate:yyyy-MM-dd}...");
            var aggregateStart = Stopwatch.StartNew();

            try
            {
                var targetDateStr = targetDate.ToString("yyyy-MM-dd");

                var allSessions = await _storageService.GetAllSessionsAsync(maxResults: 1000000);
                var targetDateSessions = allSessions.Where(s => s.StartedAt.Date == targetDate).ToList();

                if (targetDateSessions.Count == 0)
                {
                    _logger.LogInformation($"No sessions found for {targetDateStr}");
                    return;
                }

                var globalMetrics = await ComputeUsageMetricsSnapshotAsync(targetDateStr, "global", targetDateSessions);
                await _storageService.SaveUsageMetricsSnapshotAsync(globalMetrics);

                var tenantGroups = targetDateSessions.GroupBy(s => s.TenantId);
                foreach (var tenantGroup in tenantGroups)
                {
                    var tenantMetrics = await ComputeUsageMetricsSnapshotAsync(targetDateStr, tenantGroup.Key, tenantGroup.ToList());
                    await _storageService.SaveUsageMetricsSnapshotAsync(tenantMetrics);
                }

                aggregateStart.Stop();
                _logger.LogInformation($"Aggregated metrics for {targetDateSessions.Count} sessions from {targetDateStr} in {aggregateStart.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to aggregate metrics for {targetDate:yyyy-MM-dd}");
                throw;
            }
        }

        /// <summary>
        /// Computes historical metrics for a specific date and tenant
        /// </summary>
        private async Task<UsageMetricsSnapshot> ComputeUsageMetricsSnapshotAsync(string date, string tenantId, List<SessionSummary> sessions)
        {
            var computeStart = Stopwatch.StartNew();

            var completed = sessions.Where(s => s.Status == SessionStatus.Succeeded || s.Status == SessionStatus.Failed).ToList();
            var succeeded = sessions.Count(s => s.Status == SessionStatus.Succeeded);
            var successRate = completed.Count > 0 ? Math.Round((succeeded / (double)completed.Count) * 100, 1) : 0;

            var completedWithDuration = sessions.Where(s => s.DurationSeconds.HasValue && s.DurationSeconds.Value > 0).ToList();
            double avgDuration = 0, medianDuration = 0, p95Duration = 0, p99Duration = 0;

            if (completedWithDuration.Any())
            {
                var durations = completedWithDuration.Select(s => s.DurationSeconds!.Value / 60.0).OrderBy(d => d).ToList();
                avgDuration = Math.Round(durations.Average(), 1);
                medianDuration = CalculatePercentile(durations, 50);
                p95Duration = CalculatePercentile(durations, 95);
                p99Duration = CalculatePercentile(durations, 99);
            }

            var manufacturers = sessions
                .GroupBy(s => s.Manufacturer)
                .Select(g => new { Name = g.Key, Count = g.Count(), Percentage = Math.Round((g.Count() / (double)sessions.Count) * 100, 1) })
                .OrderByDescending(x => x.Count)
                .Take(5)
                .ToList();

            var models = sessions
                .GroupBy(s => s.Model)
                .Select(g => new { Name = g.Key, Count = g.Count(), Percentage = Math.Round((g.Count() / (double)sessions.Count) * 100, 1) })
                .OrderByDescending(x => x.Count)
                .Take(5)
                .ToList();

            var targetDate = DateTime.ParseExact(date, "yyyy-MM-dd", null);
            var (uniqueUsers, loginCount) = await _storageService.GetUserActivityForDateAsync(
                tenantId == "global" ? null : tenantId, targetDate);

            computeStart.Stop();

            return new UsageMetricsSnapshot
            {
                Date = date,
                TenantId = tenantId,
                ComputedAt = DateTime.UtcNow,
                ComputeDurationMs = (int)computeStart.ElapsedMilliseconds,
                SessionsTotal = sessions.Count,
                SessionsSucceeded = succeeded,
                SessionsFailed = sessions.Count(s => s.Status == SessionStatus.Failed),
                SessionsInProgress = sessions.Count(s => s.Status == SessionStatus.InProgress),
                SessionsSuccessRate = successRate,
                AvgDurationMinutes = avgDuration,
                MedianDurationMinutes = medianDuration,
                P95DurationMinutes = p95Duration,
                P99DurationMinutes = p99Duration,
                UniqueTenants = tenantId == "global" ? sessions.Select(s => s.TenantId).Distinct().Count() : 0,
                UniqueUsers = uniqueUsers,
                LoginCount = loginCount,
                TopManufacturers = JsonConvert.SerializeObject(manufacturers),
                TopModels = JsonConvert.SerializeObject(models)
            };
        }

        /// <summary>
        /// Cleans up old sessions and events based on tenant retention policies
        /// </summary>
        private async Task CleanupOldDataAsync()
        {
            _logger.LogInformation("Starting data retention cleanup...");
            var cleanupStart = Stopwatch.StartNew();

            try
            {
                var tenantIds = await _storageService.GetAllTenantIdsAsync();
                int totalSessionsDeleted = 0;
                int totalEventsDeleted = 0;

                foreach (var tenantId in tenantIds)
                {
                    try
                    {
                        var config = await _tenantConfigService.GetConfigurationAsync(tenantId);
                        var retentionDays = config?.DataRetentionDays ?? 90;
                        var cutoffDate = DateTime.UtcNow.AddDays(-retentionDays);

                        var oldSessions = await _storageService.GetSessionsOlderThanAsync(tenantId, cutoffDate);

                        if (oldSessions.Count == 0)
                        {
                            _logger.LogInformation($"Tenant {tenantId}: No sessions older than {retentionDays} days");
                            continue;
                        }

                        int sessionCount = 0;
                        int eventCount = 0;
                        int ruleResultCount = 0;
                        int appSummaryCount = 0;

                        foreach (var session in oldSessions)
                        {
                            var deletedEvents = await _storageService.DeleteSessionEventsAsync(session.TenantId, session.SessionId);
                            eventCount += deletedEvents;

                            var deletedRuleResults = await _storageService.DeleteSessionRuleResultsAsync(session.TenantId, session.SessionId);
                            ruleResultCount += deletedRuleResults;

                            var deletedAppSummaries = await _storageService.DeleteSessionAppInstallSummariesAsync(session.TenantId, session.SessionId);
                            appSummaryCount += deletedAppSummaries;

                            await _storageService.DeleteSessionAsync(session.TenantId, session.SessionId);
                            sessionCount++;
                        }

                        totalSessionsDeleted += sessionCount;
                        totalEventsDeleted += eventCount;

                        await _storageService.LogAuditEntryAsync(
                            tenantId,
                            "DataRetentionCleanup",
                            "Session",
                            $"{sessionCount} sessions",
                            "System.DailyMaintenance",
                            new Dictionary<string, string>
                            {
                                { "SessionsDeleted", sessionCount.ToString() },
                                { "EventsDeleted", eventCount.ToString() },
                                { "RuleResultsDeleted", ruleResultCount.ToString() },
                                { "AppSummariesDeleted", appSummaryCount.ToString() },
                                { "RetentionDays", retentionDays.ToString() },
                                { "CutoffDate", cutoffDate.ToString("yyyy-MM-dd") }
                            });

                        _logger.LogInformation($"Tenant {tenantId}: Deleted {sessionCount} sessions, {eventCount} events, {ruleResultCount} rule results, {appSummaryCount} app summaries (retention: {retentionDays} days)");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to cleanup data for tenant {tenantId}");
                    }
                }

                cleanupStart.Stop();
                _logger.LogInformation($"Data retention cleanup completed: {totalSessionsDeleted} sessions and {totalEventsDeleted} events deleted in {cleanupStart.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cleanup old data");
            }
        }

        /// <summary>
        /// Recomputes platform-wide stats from all tables.
        /// Used on the public landing page (no auth required).
        /// </summary>
        private async Task RecomputePlatformStatsAsync()
        {
            _logger.LogInformation("Recomputing platform stats...");
            var sw = Stopwatch.StartNew();

            try
            {
                var tenantIds = await _storageService.GetAllTenantIdsAsync();
                long totalEnrollments = 0;
                long successfulEnrollments = 0;
                long totalEvents = 0;
                long totalUsers = 0;
                var uniqueModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var tid in tenantIds)
                {
                    var sessions = await _storageService.GetSessionsAsync(tid, maxResults: 10000);
                    totalEnrollments += sessions.Count;
                    successfulEnrollments += sessions.Count(s => s.Status == SessionStatus.Succeeded);

                    foreach (var s in sessions)
                    {
                        var modelKey = $"{s.Manufacturer} {s.Model}".Trim();
                        if (!string.IsNullOrEmpty(modelKey))
                            uniqueModels.Add(modelKey);
                        totalEvents += s.EventCount;
                    }

                    var userMetrics = await _storageService.GetUserActivityMetricsAsync(tid);
                    totalUsers += userMetrics.TotalUniqueUsers;
                }

                var existingStats = await _storageService.GetPlatformStatsAsync();

                var stats = new PlatformStats
                {
                    TotalEnrollments = totalEnrollments,
                    TotalUsers = totalUsers,
                    TotalTenants = tenantIds.Count,
                    UniqueDeviceModels = uniqueModels.Count,
                    TotalEventsProcessed = totalEvents,
                    SuccessfulEnrollments = successfulEnrollments,
                    IssuesDetected = existingStats?.IssuesDetected ?? 0,
                    LastFullCompute = DateTime.UtcNow,
                    LastUpdated = DateTime.UtcNow
                };

                await _storageService.SavePlatformStatsAsync(stats);

                sw.Stop();
                _logger.LogInformation($"Platform stats recomputed in {sw.ElapsedMilliseconds}ms: " +
                    $"{totalEnrollments} enrollments, {totalUsers} users, {tenantIds.Count} tenants, {uniqueModels.Count} models");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to recompute platform stats");
            }
        }

        /// <summary>
        /// Calculates percentile value from sorted list
        /// </summary>
        private double CalculatePercentile(List<double> sortedValues, int percentile)
        {
            if (sortedValues.Count == 0) return 0;

            var index = (int)Math.Ceiling((percentile / 100.0) * sortedValues.Count) - 1;
            index = Math.Max(0, Math.Min(index, sortedValues.Count - 1));
            return Math.Round(sortedValues[index], 1);
        }
    }
}
