using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
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
        public int DevicesBlockedForExcessiveData { get; set; }
    }

    /// <summary>
    /// Dedicated service for maintenance tasks:
    /// 1. Marks stalled sessions (InProgress for too long) as timed out
    /// 2. Aggregates metrics into historical snapshots
    /// 3. Deletes old sessions and events based on tenant retention policies
    /// 4. Recomputes platform-wide stats for the landing page
    /// </summary>
    public partial class MaintenanceService
    {
        private readonly IMaintenanceRepository _maintenanceRepo;
        private readonly ISessionRepository _sessionRepo;
        private readonly IMetricsRepository _metricsRepo;
        private readonly TenantConfigurationService _tenantConfigService;
        private readonly UsageMetricsService _usageMetricsService;
        private readonly AdminConfigurationService _adminConfigurationService;
        private readonly BlockedDeviceService _blockedDeviceService;
        private readonly TenantAdminsService _tenantAdminsService;
        private readonly IUserUsageRepository _userUsageRepo;
        private readonly ILogger<MaintenanceService> _logger;

        private const string PlatformStatsAliasFileName = "platform-stats.json";
        private const string PlatformStatsAliasCacheControl = "public, max-age=300, stale-while-revalidate=86400";
        private const string PlatformStatsVersionedCacheControl = "public, max-age=31536000, immutable";

        public MaintenanceService(
            IMaintenanceRepository maintenanceRepo,
            ISessionRepository sessionRepo,
            IMetricsRepository metricsRepo,
            TenantConfigurationService tenantConfigService,
            UsageMetricsService usageMetricsService,
            AdminConfigurationService adminConfigurationService,
            BlockedDeviceService blockedDeviceService,
            TenantAdminsService tenantAdminsService,
            IUserUsageRepository userUsageRepo,
            ILogger<MaintenanceService> logger)
        {
            _maintenanceRepo = maintenanceRepo;
            _sessionRepo = sessionRepo;
            _metricsRepo = metricsRepo;
            _tenantConfigService = tenantConfigService;
            _usageMetricsService = usageMetricsService;
            _adminConfigurationService = adminConfigurationService;
            _blockedDeviceService = blockedDeviceService;
            _tenantAdminsService = tenantAdminsService;
            _userUsageRepo = userUsageRepo;
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
                await BlockExcessiveDataSendersAsync();
                await AggregateMetricsWithCatchUpAsync();
                await CleanupOldDataAsync();
                await RecomputePlatformStatsAsync();

                // Backfill and one-time repair tasks run only via manual trigger (RunManualAsync)
                // to keep the timer-triggered path lightweight. See RunManualAsync for:
                // - BackfillSessionIndexAsync (safety net for missing index entries)
                // - CleanupGhostSessionIndexEntriesAsync (one-time ghost cleanup)
                // - BackfillTenantOnboardedAtAsync (backfill missing OnboardedAt)

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

                    // --- Backfill & repair tasks (manual-only, not in timer path) ---

                    // Safety net: backfill any sessions missing from SessionsIndex
                    await _maintenanceRepo.BackfillSessionIndexAsync();

                    // One-time cleanup: remove ghost SessionsIndex entries caused by the
                    // StoreSessionAsync Replace-mode IndexRowKey bug (now fixed).
                    // TODO: Remove after 2026-06-01
                    await _maintenanceRepo.CleanupGhostSessionIndexEntriesAsync();

                    // Backfill OnboardedAt for tenants that don't have it yet
                    // TODO: Remove once all tenants have been backfilled
                    await BackfillTenantOnboardedAtAsync();
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
                var tenantIds = await _maintenanceRepo.GetAllTenantIdsAsync();
                int totalSessionsTimedOut = 0;

                foreach (var tenantId in tenantIds)
                {
                    try
                    {
                        var config = await _tenantConfigService.GetConfigurationAsync(tenantId);
                        var timeoutHours = config?.SessionTimeoutHours ?? 5;
                        var cutoffTime = DateTime.UtcNow.AddHours(-timeoutHours);

                        var stalledSessions = await _maintenanceRepo.GetStalledSessionsAsync(tenantId, cutoffTime);

                        if (stalledSessions.Count == 0)
                        {
                            _logger.LogInformation($"Tenant {tenantId}: No stalled sessions found (timeout: {timeoutHours}h)");
                            continue;
                        }

                        int sessionCount = 0;

                        foreach (var session in stalledSessions)
                        {
                            await _sessionRepo.UpdateSessionStatusAsync(
                                session.TenantId,
                                session.SessionId,
                                SessionStatus.Failed,
                                failureReason: $"Session timed out after {timeoutHours} hours (started at {session.StartedAt:yyyy-MM-dd HH:mm:ss} UTC)"
                            );
                            sessionCount++;
                        }

                        totalSessionsTimedOut += sessionCount;

                        await _maintenanceRepo.LogAuditEntryAsync(
                            tenantId,
                            "SessionTimeout",
                            "Session",
                            $"{sessionCount} sessions",
                            "System.Maintenance",
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
        /// Detects devices that are still actively sending data beyond the configured maximum session
        /// window and blocks them automatically. Status-independent: uses LastEventAt (written on every
        /// event batch) combined with StartedAt to identify sessions spanning too long a data window.
        /// Controlled by AdminConfiguration.MaxSessionWindowHours (0 = disabled).
        /// </summary>
        private async Task<int> BlockExcessiveDataSendersAsync()
        {
            _logger.LogInformation("Checking for excessive data senders...");
            var sw = Stopwatch.StartNew();

            try
            {
                var adminConfig = await _adminConfigurationService.GetConfigurationAsync();
                if (adminConfig.MaxSessionWindowHours == 0)
                {
                    _logger.LogInformation("Excessive data sender detection disabled (MaxSessionWindowHours = 0)");
                    return 0;
                }

                var windowCutoff = DateTime.UtcNow.AddHours(-adminConfig.MaxSessionWindowHours);
                var blockDurationHours = adminConfig.MaintenanceBlockDurationHours;
                var tenantIds = await _maintenanceRepo.GetAllTenantIdsAsync();
                int totalBlocked = 0;

                foreach (var tenantId in tenantIds)
                {
                    try
                    {
                        var sessions = await _maintenanceRepo.GetExcessiveDataSendersAsync(tenantId, windowCutoff, adminConfig.MaxSessionWindowHours);

                        if (sessions.Count == 0)
                        {
                            _logger.LogInformation($"Tenant {tenantId}: No excessive data sender sessions found (window: {adminConfig.MaxSessionWindowHours}h)");
                            continue;
                        }

                        int blockedCount = 0;

                        foreach (var session in sessions)
                        {
                            if (string.IsNullOrEmpty(session.SerialNumber))
                                continue;

                            var (isBlocked, _, _) = await _blockedDeviceService.IsBlockedAsync(tenantId, session.SerialNumber);
                            if (isBlocked)
                                continue;

                            var reason = $"Excessive data window: session active for >{adminConfig.MaxSessionWindowHours}h " +
                                         $"(started {session.StartedAt:yyyy-MM-dd HH:mm:ss} UTC, " +
                                         $"last event {session.LastEventAt:yyyy-MM-dd HH:mm:ss} UTC)";

                            await _blockedDeviceService.BlockDeviceAsync(
                                tenantId,
                                session.SerialNumber,
                                durationHours: blockDurationHours,
                                blockedByEmail: "System.Maintenance",
                                reason: reason);

                            blockedCount++;
                        }

                        if (blockedCount > 0)
                        {
                            await _maintenanceRepo.LogAuditEntryAsync(
                                tenantId,
                                "ExcessiveDataBlock",
                                "Device",
                                $"{blockedCount} devices",
                                "System.Maintenance",
                                new Dictionary<string, string>
                                {
                                    { "DevicesBlocked", blockedCount.ToString() },
                                    { "WindowHours", adminConfig.MaxSessionWindowHours.ToString() },
                                    { "BlockDurationHours", blockDurationHours.ToString() }
                                });

                            _logger.LogInformation($"Tenant {tenantId}: Blocked {blockedCount} excessive data sender device(s) (window: {adminConfig.MaxSessionWindowHours}h, block: {blockDurationHours}h)");
                        }

                        totalBlocked += blockedCount;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to check excessive data senders for tenant {tenantId}");
                    }
                }

                sw.Stop();
                _logger.LogInformation($"Excessive data sender check completed: {totalBlocked} devices blocked in {sw.ElapsedMilliseconds}ms");
                return totalBlocked;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check for excessive data senders");
                return 0;
            }
        }

        /// <summary>
        /// Backfills OnboardedAt for tenants that don't have it set yet.
        /// Derives the value from the earliest TenantAdmin AddedDate for each tenant.
        /// Self-healing: runs every maintenance cycle, no-ops once all tenants are backfilled.
        /// </summary>
        private async Task BackfillTenantOnboardedAtAsync()
        {
            _logger.LogInformation("Backfilling OnboardedAt for tenants...");

            try
            {
                var allConfigs = await _tenantConfigService.GetAllConfigurationsAsync();
                var configsWithoutOnboardedAt = allConfigs.Where(c => c.OnboardedAt == null).ToList();

                if (configsWithoutOnboardedAt.Count == 0)
                {
                    _logger.LogInformation("All tenants already have OnboardedAt set");
                    return;
                }

                int backfilledCount = 0;

                foreach (var config in configsWithoutOnboardedAt)
                {
                    try
                    {
                        var admins = await _tenantAdminsService.GetTenantAdminsAsync(config.TenantId);

                        if (admins.Count == 0)
                        {
                            // No admins yet — use LastUpdated as fallback (config creation date)
                            config.OnboardedAt = config.LastUpdated;
                        }
                        else
                        {
                            config.OnboardedAt = admins.Min(a => a.AddedDate);
                        }

                        await _tenantConfigService.SaveConfigurationAsync(config);
                        backfilledCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to backfill OnboardedAt for tenant {TenantId}", config.TenantId);
                    }
                }

                _logger.LogInformation("OnboardedAt backfill completed: {Count} tenants updated", backfilledCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to backfill OnboardedAt for tenants");
            }
        }

        /// <summary>
        /// Aggregates metrics for any missed days in the last 7 days, plus yesterday.
        /// Checks the UsageMetrics table for existing snapshots to avoid re-aggregation.
        /// </summary>
    }
}
