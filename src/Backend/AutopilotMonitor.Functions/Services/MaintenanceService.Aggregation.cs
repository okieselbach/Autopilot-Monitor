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
    /// Partial: Metrics aggregation, data cleanup, and platform stats recomputation.
    /// </summary>
    public partial class MaintenanceService
    {
        private async Task AggregateMetricsWithCatchUpAsync()
        {
            const int maxCatchUpDays = 7;
            var today = DateTime.UtcNow.Date;
            var aggregatedCount = 0;

            for (int daysBack = maxCatchUpDays; daysBack >= 1; daysBack--)
            {
                var date = today.AddDays(-daysBack);
                var dateStr = date.ToString("yyyy-MM-dd");

                try
                {
                    if (await _metricsRepo.HasUsageMetricsSnapshotAsync(dateStr))
                        continue;

                    _logger.LogInformation($"Catch-up: Aggregating metrics for missed date {dateStr}");
                    await AggregateMetricsForDateAsync(date);
                    aggregatedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to aggregate metrics for {dateStr} during catch-up");
                }
            }

            if (aggregatedCount > 0)
                _logger.LogInformation($"Catch-up completed: aggregated {aggregatedCount} missed day(s)");
            else
                _logger.LogInformation("No missed days to catch up on");
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

                var targetDateSessions = await _maintenanceRepo.GetSessionsByDateRangeAsync(targetDate, targetDate.AddDays(1));

                if (targetDateSessions.Count == 0)
                {
                    _logger.LogInformation($"No sessions found for {targetDateStr}");
                    return;
                }

                var globalMetrics = await ComputeUsageMetricsSnapshotAsync(targetDateStr, "global", targetDateSessions);
                await _metricsRepo.SaveUsageMetricsSnapshotAsync(globalMetrics);

                var tenantGroups = targetDateSessions.GroupBy(s => s.TenantId);
                foreach (var tenantGroup in tenantGroups)
                {
                    var tenantMetrics = await ComputeUsageMetricsSnapshotAsync(targetDateStr, tenantGroup.Key, tenantGroup.ToList());
                    await _metricsRepo.SaveUsageMetricsSnapshotAsync(tenantMetrics);
                }

                // Aggregate rule stats from RuleResults table for this date
                await AggregateRuleStatsForDateAsync(targetDate, targetDateSessions);

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
            var (uniqueUsers, loginCount) = await _metricsRepo.GetUserActivityForDateAsync(
                tenantId == "global" ? null : tenantId, targetDate);

            // App metrics: count apps per session from AppInstallSummaries table
            var sessionIdSet = new HashSet<string>(sessions.Select(s => s.SessionId));
            List<AppInstallSummary> appSummaries;
            if (tenantId == "global")
                appSummaries = await _metricsRepo.GetAllAppInstallSummariesAsync();
            else
                appSummaries = await _metricsRepo.GetAppInstallSummariesByTenantAsync(tenantId);

            var relevantApps = appSummaries.Where(a => sessionIdSet.Contains(a.SessionId)).ToList();
            var appsPerSession = relevantApps.GroupBy(a => a.SessionId).Select(g => g.Count()).ToList();
            var avgAppsPerSession = appsPerSession.Count > 0 ? Math.Round(appsPerSession.Average(), 1) : 0;
            var totalUniqueApps = relevantApps.Select(a => a.AppName).Distinct(StringComparer.OrdinalIgnoreCase).Count();

            // Script metrics: computed from session-level counters (zero extra queries)
            var totalPlatformScripts = sessions.Sum(s => s.PlatformScriptCount);
            var totalRemediationScripts = sessions.Sum(s => s.RemediationScriptCount);
            var avgPlatformScripts = sessions.Count > 0 ? Math.Round(sessions.Average(s => (double)s.PlatformScriptCount), 1) : 0;
            var avgRemediationScripts = sessions.Count > 0 ? Math.Round(sessions.Average(s => (double)s.RemediationScriptCount), 1) : 0;

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
                UserDrivenSessions = sessions.Count(s => s.IsUserDriven),
                WhiteGloveSessions = sessions.Count(s => s.IsPreProvisioned),
                UniqueUsers = uniqueUsers,
                LoginCount = loginCount,
                TopManufacturers = JsonConvert.SerializeObject(manufacturers),
                TopModels = JsonConvert.SerializeObject(models),
                AvgAppsPerSession = avgAppsPerSession,
                TotalUniqueApps = totalUniqueApps,
                AvgPlatformScriptsPerSession = avgPlatformScripts,
                AvgRemediationScriptsPerSession = avgRemediationScripts,
                TotalPlatformScripts = totalPlatformScripts,
                TotalRemediationScripts = totalRemediationScripts
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
                var tenantIds = await _maintenanceRepo.GetAllTenantIdsAsync();
                int totalSessionsDeleted = 0;
                int totalEventsDeleted = 0;

                foreach (var tenantId in tenantIds)
                {
                    try
                    {
                        var config = await _tenantConfigService.GetConfigurationAsync(tenantId);
                        var retentionDays = config?.DataRetentionDays ?? 90;

                        // 0 = infinite retention, skip cleanup for this tenant
                        if (retentionDays <= 0)
                        {
                            _logger.LogInformation($"Tenant {tenantId}: Data retention set to infinite (0), skipping cleanup");
                            continue;
                        }

                        var cutoffDate = DateTime.UtcNow.AddDays(-retentionDays);

                        var oldSessions = await _maintenanceRepo.GetSessionsOlderThanAsync(tenantId, cutoffDate);

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
                            var deletedEvents = await _maintenanceRepo.DeleteSessionEventsAsync(session.TenantId, session.SessionId);
                            eventCount += deletedEvents;

                            var deletedRuleResults = await _maintenanceRepo.DeleteSessionRuleResultsAsync(session.TenantId, session.SessionId);
                            ruleResultCount += deletedRuleResults;

                            var deletedAppSummaries = await _maintenanceRepo.DeleteSessionAppInstallSummariesAsync(session.TenantId, session.SessionId);
                            appSummaryCount += deletedAppSummaries;

                            await _sessionRepo.DeleteSessionAsync(session.TenantId, session.SessionId);
                            sessionCount++;
                        }

                        totalSessionsDeleted += sessionCount;
                        totalEventsDeleted += eventCount;

                        await _maintenanceRepo.LogAuditEntryAsync(
                            tenantId,
                            "DataRetentionCleanup",
                            "Session",
                            $"{sessionCount} sessions",
                            "System.Maintenance",
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

                // Usage data cleanup: delete UserUsageLog records older than 90 days
                await CleanupOldUsageDataAsync();

                cleanupStart.Stop();
                _logger.LogInformation($"Data retention cleanup completed: {totalSessionsDeleted} sessions and {totalEventsDeleted} events deleted in {cleanupStart.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cleanup old data");
            }
        }

        /// <summary>
        /// Reconciles rule stats for a given date by computing global aggregate rows
        /// from per-tenant rows. This ensures consistency even if real-time global
        /// increments were missed (e.g. during transient failures).
        /// </summary>
        private async Task AggregateRuleStatsForDateAsync(DateTime targetDate, List<SessionSummary> sessions)
        {
            try
            {
                var dateStr = targetDate.ToString("yyyy-MM-dd");
                // Fetch all tenant-specific rows for this date (excluding existing global rows)
                var allEntries = await _metricsRepo.GetRuleStatsAsync(startDate: dateStr, endDate: dateStr);
                var tenantEntries = allEntries.Where(e => e.TenantId != "global").ToList();

                if (tenantEntries.Count == 0)
                {
                    _logger.LogInformation("No tenant rule stats for {Date}, skipping rule stats aggregation", dateStr);
                    return;
                }

                // Group by RuleId to compute global aggregates
                var groups = tenantEntries.GroupBy(e => e.RuleId);
                int written = 0;

                foreach (var group in groups)
                {
                    var first = group.First();
                    var totalFire = group.Sum(e => e.FireCount);
                    var totalEval = group.Sum(e => e.EvaluationCount);
                    var totalSessions = group.Sum(e => e.SessionsEvaluated);
                    var totalConfSum = group.Sum(e => e.ConfidenceScoreSum);

                    var globalEntry = new RuleStatsEntry
                    {
                        Date = dateStr,
                        TenantId = "global",
                        RuleId = first.RuleId,
                        RuleType = first.RuleType,
                        RuleTitle = first.RuleTitle,
                        Category = first.Category,
                        Severity = first.Severity,
                        FireCount = totalFire,
                        EvaluationCount = totalEval,
                        SessionsEvaluated = totalSessions,
                        ConfidenceScoreSum = totalConfSum,
                        AvgConfidenceScore = totalFire > 0 ? Math.Round((double)totalConfSum / totalFire, 1) : 0,
                        UpdatedAt = DateTime.UtcNow
                    };

                    await _metricsRepo.SaveRuleStatsEntryAsync(globalEntry);
                    written++;
                }

                _logger.LogInformation("Rule stats aggregation for {Date}: reconciled {Count} global rule entries from {TenantEntries} tenant entries",
                    dateStr, written, tenantEntries.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to aggregate rule stats for {Date} (non-fatal)", targetDate.ToString("yyyy-MM-dd"));
            }
        }

        /// <summary>
        /// Deletes usage tracking records older than 90 days from UserUsageLog.
        /// </summary>
        private async Task CleanupOldUsageDataAsync()
        {
            try
            {
                var cutoffDate = DateTime.UtcNow.AddDays(-90).ToString("yyyyMMdd");
                var deleted = await _userUsageRepo.DeleteRecordsOlderThanAsync(cutoffDate);

                if (deleted > 0)
                    _logger.LogInformation("Usage data cleanup: deleted {Count} records older than 90 days (cutoff: {Cutoff})", deleted, cutoffDate);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup old usage data");
            }

            // Rule stats retention: delete entries older than 90 days
            try
            {
                var ruleStatsCutoff = DateTime.UtcNow.AddDays(-90);
                var deletedRuleStats = await _metricsRepo.DeleteRuleStatsOlderThanAsync(ruleStatsCutoff);

                if (deletedRuleStats > 0)
                    _logger.LogInformation("Rule stats cleanup: deleted {Count} entries older than 90 days", deletedRuleStats);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup old rule stats");
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
                var tenantIds = await _maintenanceRepo.GetAllTenantIdsAsync();
                var allConfigs = await _tenantConfigService.GetAllConfigurationsAsync();
                long totalEnrollments = 0;
                long successfulEnrollments = 0;
                long totalEvents = 0;
                long totalUsers = 0;
                var uniqueModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var tid in tenantIds)
                {
                    var sessionsPage = await _sessionRepo.GetSessionsAsync(tid, maxResults: 10000);
                    var sessions = sessionsPage.Sessions;
                    totalEnrollments += sessions.Count;
                    successfulEnrollments += sessions.Count(s => s.Status == SessionStatus.Succeeded);

                    foreach (var s in sessions)
                    {
                        var modelKey = $"{s.Manufacturer} {s.Model}".Trim();
                        if (!string.IsNullOrEmpty(modelKey))
                            uniqueModels.Add(modelKey);
                        totalEvents += s.EventCount;
                    }

                    var userMetrics = await _metricsRepo.GetUserActivityMetricsAsync(tid);
                    totalUsers += userMetrics.TotalUniqueUsers;
                }

                var existingStats = await _metricsRepo.GetPlatformStatsAsync();

                var stats = new PlatformStats
                {
                    TotalEnrollments = totalEnrollments,
                    TotalUsers = totalUsers,
                    TotalTenants = tenantIds.Count,
                    TotalSignedUpTenants = allConfigs.Count,
                    UniqueDeviceModels = uniqueModels.Count,
                    TotalEventsProcessed = totalEvents,
                    SuccessfulEnrollments = successfulEnrollments,
                    IssuesDetected = existingStats?.IssuesDetected ?? 0,
                    LastFullCompute = DateTime.UtcNow,
                    LastUpdated = DateTime.UtcNow
                };

                await _metricsRepo.SavePlatformStatsAsync(stats);
                await TryPublishPlatformStatsJsonAsync(stats);

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
        /// Publishes versioned platform stats JSON + alias manifest to Blob Storage.
        /// This must never fail maintenance execution.
        /// </summary>
        private async Task TryPublishPlatformStatsJsonAsync(PlatformStats stats)
        {
            try
            {
                var adminConfig = await _adminConfigurationService.GetConfigurationAsync();
                var containerSasUrl = adminConfig.PlatformStatsBlobSasUrl?.Trim();

                if (string.IsNullOrWhiteSpace(containerSasUrl))
                {
                    _logger.LogInformation("Skipping platform stats JSON publish: PlatformStatsBlobSasUrl is not configured.");
                    return;
                }

                var containerClient = new BlobContainerClient(new Uri(containerSasUrl));
                var generatedAtUtc = DateTime.UtcNow;
                var versionedFileName = $"platform-stats.{generatedAtUtc:yyyy-MM-dd}.json";

                var versionedPayload = new
                {
                    totalEnrollments = stats.TotalEnrollments,
                    totalUsers = stats.TotalUsers,
                    totalTenants = stats.TotalTenants,
                    totalSignedUpTenants = stats.TotalSignedUpTenants,
                    uniqueDeviceModels = stats.UniqueDeviceModels,
                    totalEventsProcessed = stats.TotalEventsProcessed,
                    successfulEnrollments = stats.SuccessfulEnrollments,
                    issuesDetected = stats.IssuesDetected,
                    lastFullCompute = stats.LastFullCompute,
                    lastUpdated = stats.LastUpdated
                };

                var aliasPayload = new
                {
                    latest = versionedFileName,
                    generatedAtUtc = generatedAtUtc.ToString("o")
                };

                await UploadJsonBlobAsync(containerClient, versionedFileName, versionedPayload, PlatformStatsVersionedCacheControl);
                await UploadJsonBlobAsync(containerClient, PlatformStatsAliasFileName, aliasPayload, PlatformStatsAliasCacheControl);

                _logger.LogInformation(
                    "Published platform stats JSON blobs: versioned={VersionedFile} and alias={AliasFile}",
                    versionedFileName,
                    PlatformStatsAliasFileName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish platform stats JSON to Blob Storage. Maintenance continues.");
            }
        }

        private async Task UploadJsonBlobAsync(BlobContainerClient containerClient, string blobName, object payload, string cacheControl)
        {
            var blobClient = containerClient.GetBlobClient(blobName);
            var json = JsonConvert.SerializeObject(payload);
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

            await blobClient.UploadAsync(stream, overwrite: true);
            await blobClient.SetHttpHeadersAsync(new BlobHttpHeaders
            {
                ContentType = "application/json; charset=utf-8",
                CacheControl = cacheControl
            });
            await blobClient.SetAccessTierAsync(AccessTier.Hot);
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

        /// <summary>
        /// Removes distress reports older than 14 days. Distress data is unverified
        /// and low-volume; short retention keeps Table Storage lean.
        /// </summary>
        private async Task CleanupOldDistressReportsAsync()
        {
            const int retentionDays = 14;
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
            _logger.LogInformation("Starting distress report cleanup (retention: {Days} days, cutoff: {Cutoff:yyyy-MM-dd})", retentionDays, cutoff);

            try
            {
                var tenantIds = await _maintenanceRepo.GetAllTenantIdsAsync();
                var totalDeleted = 0;

                foreach (var tenantId in tenantIds)
                {
                    try
                    {
                        var deleted = await _distressReportRepo.DeleteDistressReportsOlderThanAsync(tenantId, cutoff);
                        if (deleted > 0)
                        {
                            totalDeleted += deleted;
                            _logger.LogInformation("Tenant {TenantId}: Deleted {Count} old distress reports", tenantId, deleted);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to cleanup distress reports for tenant {TenantId}", tenantId);
                    }
                }

                _logger.LogInformation("Distress report cleanup complete: {Total} reports deleted across all tenants", totalDeleted);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Distress report cleanup failed");
            }
        }

        /// <summary>
        /// Verifies that agent binaries and bootstrap script are available on blob storage.
        /// Records an OpsEvent for each missing item so Global Admins see it in the dashboard.
        /// </summary>
        private async Task CheckAgentBlobStorageAsync()
        {
            _logger.LogInformation("Checking agent blob storage availability...");

            var zipUrl = $"{AutopilotMonitor.Shared.Constants.AgentBlobBaseUrl}/{AutopilotMonitor.Shared.Constants.AgentZipFileName}";
            var ps1Url = $"{AutopilotMonitor.Shared.Constants.AgentBlobBaseUrl}/Install-AutopilotMonitor.ps1";

            try
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(15);

                var zipRequest = new HttpRequestMessage(HttpMethod.Head, zipUrl);
                var ps1Request = new HttpRequestMessage(HttpMethod.Head, ps1Url);

                var results = await Task.WhenAll(
                    client.SendAsync(zipRequest),
                    client.SendAsync(ps1Request)
                );

                var zipResponse = results[0];
                var ps1Response = results[1];

                if (!zipResponse.IsSuccessStatusCode)
                {
                    _logger.LogError("Agent ZIP not available on blob storage: HTTP {StatusCode}", (int)zipResponse.StatusCode);
                    await _opsEventService.RecordBlobStorageMissingAsync("Agent ZIP (AutopilotMonitor-Agent.zip)", (int)zipResponse.StatusCode);
                }

                if (!ps1Response.IsSuccessStatusCode)
                {
                    _logger.LogError("Bootstrap script not available on blob storage: HTTP {StatusCode}", (int)ps1Response.StatusCode);
                    await _opsEventService.RecordBlobStorageMissingAsync("Bootstrap script (Install-AutopilotMonitor.ps1)", (int)ps1Response.StatusCode);
                }

                if (zipResponse.IsSuccessStatusCode && ps1Response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Agent blob storage check passed: all binaries available");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent blob storage check failed — storage unreachable");
                await _opsEventService.RecordBlobStorageUnreachableAsync(ex.Message);
            }
        }

        /// <summary>
        /// Removes operational events older than the configured retention period.
        /// Retention is controlled by AdminConfiguration.OpsEventRetentionDays (default: 90).
        /// </summary>
        private async Task CleanupOldOpsEventsAsync()
        {
            try
            {
                var adminConfig = await _adminConfigurationService.GetConfigurationAsync();
                var retentionDays = adminConfig.OpsEventRetentionDays;

                if (retentionDays <= 0)
                {
                    _logger.LogInformation("OpsEvents cleanup disabled (OpsEventRetentionDays = 0)");
                    return;
                }

                var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
                _logger.LogInformation("Starting OpsEvents cleanup (retention: {Days} days, cutoff: {Cutoff:yyyy-MM-dd})", retentionDays, cutoff);

                var deleted = await _opsEventRepo.DeleteOpsEventsOlderThanAsync(cutoff);
                _logger.LogInformation("OpsEvents cleanup complete: {Deleted} events deleted", deleted);

                if (deleted > 0)
                {
                    await _opsEventService.RecordOpsEventCleanupAsync(deleted, retentionDays);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OpsEvents cleanup failed");
            }
        }
    }
}

