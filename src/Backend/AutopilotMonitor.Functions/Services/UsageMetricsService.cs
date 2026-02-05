using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Service for computing platform usage metrics
    /// </summary>
    public class UsageMetricsService
    {
        private readonly TableStorageService _storageService;
        private readonly ILogger<UsageMetricsService> _logger;

        // In-memory cache
        private static PlatformUsageMetrics? _cachedMetrics;
        private static DateTime _cacheExpiry = DateTime.MinValue;
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
        private static readonly object _cacheLock = new object();

        public UsageMetricsService(
            TableStorageService storageService,
            ILogger<UsageMetricsService> logger)
        {
            _storageService = storageService;
            _logger = logger;
        }

        /// <summary>
        /// Computes platform usage metrics (with 5-minute cache)
        /// </summary>
        public async Task<PlatformUsageMetrics> ComputeUsageMetricsAsync()
        {
            // Check cache first
            lock (_cacheLock)
            {
                if (_cachedMetrics != null && DateTime.UtcNow < _cacheExpiry)
                {
                    _logger.LogInformation("Returning cached usage metrics (expires in {Seconds}s)",
                        (_cacheExpiry - DateTime.UtcNow).TotalSeconds);
                    _cachedMetrics.FromCache = true;
                    return _cachedMetrics;
                }
            }

            // Compute fresh metrics
            _logger.LogInformation("Computing fresh usage metrics...");
            var stopwatch = Stopwatch.StartNew();

            var metrics = await ComputeUsageMetricsInternalAsync();

            stopwatch.Stop();
            metrics.ComputeDurationMs = (int)stopwatch.ElapsedMilliseconds;
            metrics.ComputedAt = DateTime.UtcNow;
            metrics.FromCache = false;

            _logger.LogInformation("Usage metrics computed in {Ms}ms", metrics.ComputeDurationMs);

            // Update cache
            lock (_cacheLock)
            {
                _cachedMetrics = metrics;
                _cacheExpiry = DateTime.UtcNow.Add(CacheDuration);
            }

            return metrics;
        }

        /// <summary>
        /// Computes tenant-specific usage metrics (no caching for tenant-specific metrics)
        /// </summary>
        public async Task<PlatformUsageMetrics> ComputeTenantUsageMetricsAsync(string tenantId)
        {
            _logger.LogInformation($"Computing usage metrics for tenant {tenantId}...");
            var stopwatch = Stopwatch.StartNew();

            var metrics = await ComputeTenantUsageMetricsInternalAsync(tenantId);

            stopwatch.Stop();
            metrics.ComputeDurationMs = (int)stopwatch.ElapsedMilliseconds;
            metrics.ComputedAt = DateTime.UtcNow;
            metrics.FromCache = false;

            _logger.LogInformation($"Tenant usage metrics computed in {metrics.ComputeDurationMs}ms");

            return metrics;
        }

        private async Task<PlatformUsageMetrics> ComputeUsageMetricsInternalAsync()
        {
            // Query all sessions (galactic admin only!)
            // Note: This queries ALL sessions across ALL tenants - can handle millions of records
            // Since this is on-demand only, we can afford to process large datasets
            var allSessions = await _storageService.GetAllSessionsAsync(maxResults: 1000000);

            var now = DateTime.UtcNow;
            var today = now.Date;
            var last7Days = now.AddDays(-7);
            var last30Days = now.AddDays(-30);

            // Session Metrics
            var sessionMetrics = new SessionMetrics
            {
                Total = allSessions.Count,
                Today = allSessions.Count(s => s.StartedAt >= today),
                Last7Days = allSessions.Count(s => s.StartedAt >= last7Days),
                Last30Days = allSessions.Count(s => s.StartedAt >= last30Days),
                Succeeded = allSessions.Count(s => s.Status == SessionStatus.Succeeded),
                Failed = allSessions.Count(s => s.Status == SessionStatus.Failed),
                InProgress = allSessions.Count(s => s.Status == SessionStatus.InProgress),
                SuccessRate = CalculateSuccessRate(allSessions)
            };

            // Tenant Metrics
            var tenantMetrics = new TenantMetrics
            {
                Total = allSessions.Select(s => s.TenantId).Distinct().Count(),
                Active7Days = allSessions
                    .Where(s => s.StartedAt >= last7Days)
                    .Select(s => s.TenantId)
                    .Distinct()
                    .Count(),
                Active30Days = allSessions
                    .Where(s => s.StartedAt >= last30Days)
                    .Select(s => s.TenantId)
                    .Distinct()
                    .Count()
            };

            // User Metrics (placeholder until Entra ID auth is implemented)
            var userMetrics = new UserMetrics
            {
                Total = 0,
                DailyLogins = 0,
                Active7Days = 0,
                Active30Days = 0,
                Note = "User metrics require Entra ID authentication (coming soon)"
            };

            // Performance Metrics
            var completedSessions = allSessions.Where(s => s.DurationSeconds.HasValue && s.DurationSeconds.Value > 0).ToList();
            var performanceMetrics = new PerformanceMetrics();

            if (completedSessions.Any())
            {
                var durations = completedSessions.Select(s => s.DurationSeconds!.Value / 60.0).OrderBy(d => d).ToList();
                performanceMetrics.AvgDurationMinutes = Math.Round(durations.Average(), 1);
                performanceMetrics.MedianDurationMinutes = CalculatePercentile(durations, 50);
                performanceMetrics.P95DurationMinutes = CalculatePercentile(durations, 95);
                performanceMetrics.P99DurationMinutes = CalculatePercentile(durations, 99);
            }

            // Hardware Metrics
            var totalCount = allSessions.Count;
            var hardwareMetrics = new HardwareMetrics
            {
                TopManufacturers = allSessions
                    .GroupBy(s => s.Manufacturer)
                    .Select(g => new HardwareCount
                    {
                        Name = g.Key,
                        Count = g.Count(),
                        Percentage = Math.Round((g.Count() / (double)totalCount) * 100, 1)
                    })
                    .OrderByDescending(x => x.Count)
                    .Take(10)
                    .ToList(),

                TopModels = allSessions
                    .GroupBy(s => s.Model)
                    .Select(g => new HardwareCount
                    {
                        Name = g.Key,
                        Count = g.Count(),
                        Percentage = Math.Round((g.Count() / (double)totalCount) * 100, 1)
                    })
                    .OrderByDescending(x => x.Count)
                    .Take(10)
                    .ToList()
            };

            return new PlatformUsageMetrics
            {
                Sessions = sessionMetrics,
                Tenants = tenantMetrics,
                Users = userMetrics,
                Performance = performanceMetrics,
                Hardware = hardwareMetrics
            };
        }

        private async Task<PlatformUsageMetrics> ComputeTenantUsageMetricsInternalAsync(string tenantId)
        {
            // Query sessions for specific tenant only
            var tenantSessions = await _storageService.GetSessionsAsync(tenantId, maxResults: 1000000);

            var now = DateTime.UtcNow;
            var today = now.Date;
            var last7Days = now.AddDays(-7);
            var last30Days = now.AddDays(-30);

            // Session Metrics
            var sessionMetrics = new SessionMetrics
            {
                Total = tenantSessions.Count,
                Today = tenantSessions.Count(s => s.StartedAt >= today),
                Last7Days = tenantSessions.Count(s => s.StartedAt >= last7Days),
                Last30Days = tenantSessions.Count(s => s.StartedAt >= last30Days),
                Succeeded = tenantSessions.Count(s => s.Status == SessionStatus.Succeeded),
                Failed = tenantSessions.Count(s => s.Status == SessionStatus.Failed),
                InProgress = tenantSessions.Count(s => s.Status == SessionStatus.InProgress),
                SuccessRate = CalculateSuccessRate(tenantSessions)
            };

            // Tenant Metrics (always 1 for tenant-specific view)
            var tenantMetrics = new TenantMetrics
            {
                Total = 1,
                Active7Days = tenantSessions.Any(s => s.StartedAt >= last7Days) ? 1 : 0,
                Active30Days = tenantSessions.Any(s => s.StartedAt >= last30Days) ? 1 : 0
            };

            // User Metrics (placeholder until Entra ID auth is implemented)
            var userMetrics = new UserMetrics
            {
                Total = 0,
                DailyLogins = 0,
                Active7Days = 0,
                Active30Days = 0,
                Note = "User metrics require Entra ID authentication (coming soon)"
            };

            // Performance Metrics
            var completedSessions = tenantSessions.Where(s => s.DurationSeconds.HasValue && s.DurationSeconds.Value > 0).ToList();
            var performanceMetrics = new PerformanceMetrics();

            if (completedSessions.Any())
            {
                var durations = completedSessions.Select(s => s.DurationSeconds!.Value / 60.0).OrderBy(d => d).ToList();
                performanceMetrics.AvgDurationMinutes = Math.Round(durations.Average(), 1);
                performanceMetrics.MedianDurationMinutes = CalculatePercentile(durations, 50);
                performanceMetrics.P95DurationMinutes = CalculatePercentile(durations, 95);
                performanceMetrics.P99DurationMinutes = CalculatePercentile(durations, 99);
            }

            // Hardware Metrics
            var totalCount = tenantSessions.Count;
            var hardwareMetrics = new HardwareMetrics();

            if (totalCount > 0)
            {
                hardwareMetrics.TopManufacturers = tenantSessions
                    .GroupBy(s => s.Manufacturer)
                    .Select(g => new HardwareCount
                    {
                        Name = g.Key,
                        Count = g.Count(),
                        Percentage = Math.Round((g.Count() / (double)totalCount) * 100, 1)
                    })
                    .OrderByDescending(x => x.Count)
                    .Take(10)
                    .ToList();

                hardwareMetrics.TopModels = tenantSessions
                    .GroupBy(s => s.Model)
                    .Select(g => new HardwareCount
                    {
                        Name = g.Key,
                        Count = g.Count(),
                        Percentage = Math.Round((g.Count() / (double)totalCount) * 100, 1)
                    })
                    .OrderByDescending(x => x.Count)
                    .Take(10)
                    .ToList();
            }

            return new PlatformUsageMetrics
            {
                Sessions = sessionMetrics,
                Tenants = tenantMetrics,
                Users = userMetrics,
                Performance = performanceMetrics,
                Hardware = hardwareMetrics
            };
        }

        private double CalculateSuccessRate(List<SessionSummary> sessions)
        {
            var completed = sessions.Count(s => s.Status == SessionStatus.Succeeded || s.Status == SessionStatus.Failed);
            if (completed == 0) return 0;

            var succeeded = sessions.Count(s => s.Status == SessionStatus.Succeeded);
            return Math.Round((succeeded / (double)completed) * 100, 1);
        }

        private double CalculatePercentile(List<double> sortedValues, int percentile)
        {
            if (sortedValues.Count == 0) return 0;

            var index = (int)Math.Ceiling((percentile / 100.0) * sortedValues.Count) - 1;
            index = Math.Max(0, Math.Min(index, sortedValues.Count - 1));
            return Math.Round(sortedValues[index], 1);
        }
    }
}
