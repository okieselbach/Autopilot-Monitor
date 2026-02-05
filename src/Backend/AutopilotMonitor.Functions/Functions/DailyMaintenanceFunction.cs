using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Functions
{
    /// <summary>
    /// Daily maintenance job that:
    /// 1. Marks stalled sessions (InProgress for too long) as timed out
    /// 2. Aggregates yesterday's metrics into historical snapshots
    /// 3. Deletes old sessions and events based on tenant retention policies
    /// Runs daily at 2 AM UTC
    /// </summary>
    public class DailyMaintenanceFunction
    {
        private readonly TableStorageService _storageService;
        private readonly TenantConfigurationService _tenantConfigService;
        private readonly UsageMetricsService _usageMetricsService;
        private readonly ILogger<DailyMaintenanceFunction> _logger;

        public DailyMaintenanceFunction(
            TableStorageService storageService,
            TenantConfigurationService tenantConfigService,
            UsageMetricsService usageMetricsService,
            ILogger<DailyMaintenanceFunction> logger)
        {
            _storageService = storageService;
            _tenantConfigService = tenantConfigService;
            _usageMetricsService = usageMetricsService;
            _logger = logger;
        }

        /// <summary>
        /// Timer trigger: Runs daily at 2:00 AM UTC
        /// NCRONTAB format: {second} {minute} {hour} {day} {month} {day-of-week}
        /// </summary>
        [Function("DailyMaintenance")]
        public async Task Run([TimerTrigger("0 0 2 * * *")] object timer)
        {
            _logger.LogInformation($"Daily maintenance started at {DateTime.UtcNow}");
            var maintenanceStart = Stopwatch.StartNew();

            try
            {
                // Step 1: Mark stalled sessions as timed out
                await MarkStalledSessionsAsTimedOutAsync();

                // Step 2: Aggregate yesterday's metrics into historical snapshots
                await AggregateYesterdayMetricsAsync();

                // Step 3: Clean up old data based on retention policies
                await CleanupOldDataAsync();

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
                    // Step 1: Mark stalled sessions as timed out
                    await MarkStalledSessionsAsTimedOutAsync();
                    result.StalledSessionsChecked = true;
                }

                // Step 2: Aggregate metrics for specified date (or yesterday if not specified)
                var dateToAggregate = targetDate ?? DateTime.UtcNow.AddDays(-1).Date;
                await AggregateMetricsForDateAsync(dateToAggregate);
                result.AggregatedDate = dateToAggregate.ToString("yyyy-MM-dd");
                result.MetricsAggregated = true;

                if (!aggregateOnly)
                {
                    // Step 3: Clean up old data based on retention policies
                    await CleanupOldDataAsync();
                    result.DataCleanupExecuted = true;
                }

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
        /// This prevents stalled sessions from running indefinitely and skewing statistics
        /// </summary>
        private async Task MarkStalledSessionsAsTimedOutAsync()
        {
            _logger.LogInformation("Checking for stalled sessions...");
            var stalledStart = Stopwatch.StartNew();

            try
            {
                // Get all unique tenant IDs
                var tenantIds = await _storageService.GetAllTenantIdsAsync();

                int totalSessionsTimedOut = 0;

                foreach (var tenantId in tenantIds)
                {
                    try
                    {
                        // Get tenant configuration to determine session timeout
                        var config = await _tenantConfigService.GetConfigurationAsync(tenantId);
                        var timeoutHours = config?.SessionTimeoutHours ?? 5; // Default 5 hours if not configured

                        var cutoffTime = DateTime.UtcNow.AddHours(-timeoutHours);

                        // Get all sessions for this tenant
                        var sessions = await _storageService.GetSessionsAsync(tenantId, maxResults: 1000000);

                        // Find sessions that are still in progress but started before cutoff time
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
                            // Mark session as failed with timeout reason
                            await _storageService.UpdateSessionStatusAsync(
                                session.TenantId,
                                session.SessionId,
                                SessionStatus.Failed,
                                failureReason: $"Session timed out after {timeoutHours} hours (started at {session.StartedAt:yyyy-MM-dd HH:mm:ss} UTC)"
                            );
                            sessionCount++;
                        }

                        totalSessionsTimedOut += sessionCount;

                        // Log audit entry
                        await _storageService.LogAuditEntryAsync(
                            tenantId,
                            "SessionTimeout",
                            "Session",
                            $"{sessionCount} sessions",
                            "System.DailyMaintenance",
                            new System.Collections.Generic.Dictionary<string, string>
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

                // Get all sessions from target date
                var allSessions = await _storageService.GetAllSessionsAsync(maxResults: 1000000);
                var targetDateSessions = allSessions.Where(s => s.StartedAt.Date == targetDate).ToList();

                if (targetDateSessions.Count == 0)
                {
                    _logger.LogInformation($"No sessions found for {targetDateStr}");
                    return;
                }

                // Aggregate global metrics (cross-tenant)
                var globalMetrics = ComputeUsageMetricsSnapshot(targetDateStr, "global", targetDateSessions);
                await _storageService.SaveUsageMetricsSnapshotAsync(globalMetrics);

                // Aggregate per-tenant metrics
                var tenantGroups = targetDateSessions.GroupBy(s => s.TenantId);
                foreach (var tenantGroup in tenantGroups)
                {
                    var tenantMetrics = ComputeUsageMetricsSnapshot(targetDateStr, tenantGroup.Key, tenantGroup.ToList());
                    await _storageService.SaveUsageMetricsSnapshotAsync(tenantMetrics);
                }

                aggregateStart.Stop();
                _logger.LogInformation($"Aggregated metrics for {targetDateSessions.Count} sessions from {targetDateStr} in {aggregateStart.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to aggregate metrics for {targetDate:yyyy-MM-dd}");
                throw; // Re-throw for manual trigger error handling
            }
        }

        /// <summary>
        /// Computes historical metrics for a specific date and tenant
        /// </summary>
        private UsageMetricsSnapshot ComputeUsageMetricsSnapshot(string date, string tenantId, List<SessionSummary> sessions)
        {
            var computeStart = Stopwatch.StartNew();

            // Session statistics
            var completed = sessions.Where(s => s.Status == SessionStatus.Succeeded || s.Status == SessionStatus.Failed).ToList();
            var succeeded = sessions.Count(s => s.Status == SessionStatus.Succeeded);
            var successRate = completed.Count > 0 ? Math.Round((succeeded / (double)completed.Count) * 100, 1) : 0;

            // Performance statistics
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

            // Hardware statistics (top 5 only)
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
                UniqueUsers = 0, // TODO: Implement with Entra ID
                LoginCount = 0,  // TODO: Implement with Entra ID
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
                // Get all unique tenant IDs
                var tenantIds = await _storageService.GetAllTenantIdsAsync();

                int totalSessionsDeleted = 0;
                int totalEventsDeleted = 0;

                foreach (var tenantId in tenantIds)
                {
                    try
                    {
                        // Get tenant configuration to determine retention period
                        var config = await _tenantConfigService.GetConfigurationAsync(tenantId);
                        var retentionDays = config?.DataRetentionDays ?? 90; // Default 90 days if not configured

                        var cutoffDate = DateTime.UtcNow.AddDays(-retentionDays);

                        // Find sessions older than retention period
                        var oldSessions = await _storageService.GetSessionsOlderThanAsync(tenantId, cutoffDate);

                        if (oldSessions.Count == 0)
                        {
                            _logger.LogInformation($"Tenant {tenantId}: No sessions older than {retentionDays} days");
                            continue;
                        }

                        // Delete each session and its events
                        int sessionCount = 0;
                        int eventCount = 0;

                        foreach (var session in oldSessions)
                        {
                            // Delete all events for this session (no date check needed!)
                            var deletedEvents = await _storageService.DeleteSessionEventsAsync(session.TenantId, session.SessionId);
                            eventCount += deletedEvents;

                            // Delete the session itself
                            await _storageService.DeleteSessionAsync(session.TenantId, session.SessionId);
                            sessionCount++;
                        }

                        totalSessionsDeleted += sessionCount;
                        totalEventsDeleted += eventCount;

                        // Log audit entry
                        await _storageService.LogAuditEntryAsync(
                            tenantId,
                            "DataRetentionCleanup",
                            "Session",
                            $"{sessionCount} sessions",
                            "System.DailyMaintenance",
                            new System.Collections.Generic.Dictionary<string, string>
                            {
                                { "SessionsDeleted", sessionCount.ToString() },
                                { "EventsDeleted", eventCount.ToString() },
                                { "RetentionDays", retentionDays.ToString() },
                                { "CutoffDate", cutoffDate.ToString("yyyy-MM-dd") }
                            });

                        _logger.LogInformation($"Tenant {tenantId}: Deleted {sessionCount} sessions and {eventCount} events (retention: {retentionDays} days)");
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
    }
}
