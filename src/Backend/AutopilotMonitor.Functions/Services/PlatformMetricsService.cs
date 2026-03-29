using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Service for computing platform agent metrics (CPU, memory, network per session).
    /// Fetches sessions + their events server-side and caches the result for 5 minutes.
    /// </summary>
    public class PlatformMetricsService
    {
        private readonly ISessionRepository _sessionRepo;
        private readonly ILogger<PlatformMetricsService> _logger;

        // In-memory cache (same pattern as UsageMetricsService)
        private static PlatformAgentMetricsResponse? _cachedMetrics;
        private static DateTime _cacheExpiry = DateTime.MinValue;
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
        private static readonly object _cacheLock = new object();

        public PlatformMetricsService(
            ISessionRepository sessionRepo,
            ILogger<PlatformMetricsService> logger)
        {
            _sessionRepo = sessionRepo;
            _logger = logger;
        }

        /// <summary>
        /// Computes platform agent metrics (with 5-minute cache)
        /// </summary>
        public async Task<PlatformAgentMetricsResponse> ComputePlatformMetricsAsync()
        {
            // Check cache first
            lock (_cacheLock)
            {
                if (_cachedMetrics != null && DateTime.UtcNow < _cacheExpiry)
                {
                    _logger.LogInformation("Returning cached platform metrics (expires in {Seconds}s)",
                        (_cacheExpiry - DateTime.UtcNow).TotalSeconds);
                    _cachedMetrics.FromCache = true;
                    return _cachedMetrics;
                }
            }

            // Compute fresh metrics
            _logger.LogInformation("Computing fresh platform agent metrics...");
            var stopwatch = Stopwatch.StartNew();

            var metrics = await ComputePlatformMetricsInternalAsync();

            stopwatch.Stop();
            metrics.ComputeDurationMs = (int)stopwatch.ElapsedMilliseconds;
            metrics.ComputedAt = DateTime.UtcNow;
            metrics.FromCache = false;

            _logger.LogInformation("Platform agent metrics computed in {Ms}ms", metrics.ComputeDurationMs);

            // Update cache
            lock (_cacheLock)
            {
                _cachedMetrics = metrics;
                _cacheExpiry = DateTime.UtcNow.Add(CacheDuration);
            }

            return metrics;
        }

        private async Task<PlatformAgentMetricsResponse> ComputePlatformMetricsInternalAsync()
        {
            // 1. Fetch the 100 most recent sessions across all tenants
            var page = await _sessionRepo.GetAllSessionsAsync(maxResults: 100);
            var allSessions = page.Sessions;

            if (allSessions.Count == 0)
            {
                return new PlatformAgentMetricsResponse { Sessions = new List<SessionAgentMetric>() };
            }

            // 2. For each session, fetch events and extract agent_metrics_snapshot data
            var sessionMetrics = new List<SessionAgentMetric>();

            var tasks = allSessions.Select(async session =>
            {
                try
                {
                    var events = await _sessionRepo.GetSessionEventsAsync(session.TenantId, session.SessionId);
                    var snapshots = events
                        .Where(e => e.EventType == "agent_metrics_snapshot" && e.Data != null)
                        .Select(e => e.Data)
                        .ToList();

                    if (snapshots.Count == 0) return null;

                    var cpuValues = snapshots.Select(s => GetDouble(s, "agent_cpu_percent")).ToList();
                    var wsValues = snapshots.Select(s => GetDouble(s, "agent_working_set_mb")).ToList();
                    var pbValues = snapshots.Select(s => GetDouble(s, "agent_private_bytes_mb")).ToList();
                    var latValues = snapshots.Select(s => GetDouble(s, "net_avg_latency_ms")).Where(v => v > 0).ToList();
                    var spoolValues = snapshots.Select(s => GetDouble(s, "spool_queue_depth")).ToList();

                    var lastSnapshot = snapshots.Last();

                    // Resolve agent version: prefer from snapshot, fallback to session
                    var agentVersion = snapshots
                        .Select(s => GetString(s, "agent_version"))
                        .FirstOrDefault(v => !string.IsNullOrEmpty(v))
                        ?? session.AgentVersion
                        ?? "unknown";

                    return new SessionAgentMetric
                    {
                        SessionId = session.SessionId,
                        TenantId = session.TenantId,
                        DeviceName = session.DeviceName,
                        Manufacturer = session.Manufacturer,
                        Model = session.Model,
                        StartedAt = session.StartedAt.ToString("o"),
                        Status = session.Status.ToString(),
                        AgentVersion = agentVersion,
                        SnapshotCount = snapshots.Count,
                        TotalBytesUp = GetDouble(lastSnapshot, "net_total_bytes_up"),
                        TotalBytesDown = GetDouble(lastSnapshot, "net_total_bytes_down"),
                        TotalRequests = GetDouble(lastSnapshot, "net_total_requests"),
                        AvgCpu = cpuValues.Count > 0 ? cpuValues.Average() : 0,
                        MaxCpu = cpuValues.Count > 0 ? cpuValues.Max() : 0,
                        AvgWorkingSet = wsValues.Count > 0 ? wsValues.Average() : 0,
                        MaxWorkingSet = wsValues.Count > 0 ? wsValues.Max() : 0,
                        AvgPrivateBytes = pbValues.Count > 0 ? pbValues.Average() : 0,
                        AvgLatency = latValues.Count > 0 ? latValues.Average() : 0,
                        AvgSpoolDepth = spoolValues.Count > 0 ? spoolValues.Average() : 0,
                        MaxSpoolDepth = spoolValues.Count > 0 ? spoolValues.Max() : 0
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch events for session {SessionId}", session.SessionId);
                    return null;
                }
            }).ToList();

            var results = await Task.WhenAll(tasks);
            sessionMetrics = results.Where(r => r != null).ToList()!;

            // Compute delivery latency from all events across sessions
            var deliveryLatency = ComputeDeliveryLatency(allSessions, _sessionRepo);
            var crashRate = ComputeCrashRate(allSessions, _sessionRepo);

            // Run both in parallel
            await Task.WhenAll(deliveryLatency, crashRate);

            return new PlatformAgentMetricsResponse
            {
                Sessions = sessionMetrics,
                DeliveryLatency = deliveryLatency.Result,
                CrashRate = crashRate.Result
            };
        }

        private async Task<DeliveryLatencyMetrics> ComputeDeliveryLatency(
            List<AutopilotMonitor.Shared.Models.SessionSummary> sessions,
            ISessionRepository sessionRepo)
        {
            try
            {
                // Sample up to 20 recent sessions for latency data
                var sampleSessions = sessions.Take(20).ToList();
                var allDeltas = new List<double>();

                var latencyTasks = sampleSessions.Select(async session =>
                {
                    try
                    {
                        var events = await sessionRepo.GetSessionEventsAsync(session.TenantId, session.SessionId);
                        return events
                            .Where(e => e.ReceivedAt.HasValue && e.Timestamp != default)
                            .Select(e => (e.ReceivedAt!.Value - e.Timestamp).TotalMilliseconds)
                            .ToList();
                    }
                    catch { return new List<double>(); }
                }).ToList();

                var latencyResults = await Task.WhenAll(latencyTasks);
                foreach (var deltas in latencyResults)
                    allDeltas.AddRange(deltas);

                if (allDeltas.Count == 0)
                    return new DeliveryLatencyMetrics();

                var negativeCount = allDeltas.Count(d => d < 0);
                var validDeltas = allDeltas.Where(d => d >= 0).OrderBy(d => d).ToList();

                if (validDeltas.Count == 0)
                    return new DeliveryLatencyMetrics
                    {
                        SampleCount = allDeltas.Count,
                        ClockSkewPercent = 100.0
                    };

                return new DeliveryLatencyMetrics
                {
                    P50Ms = Math.Round(Percentile(validDeltas, 0.50), 0),
                    P95Ms = Math.Round(Percentile(validDeltas, 0.95), 0),
                    P99Ms = Math.Round(Percentile(validDeltas, 0.99), 0),
                    AvgMs = Math.Round(validDeltas.Average(), 0),
                    SampleCount = allDeltas.Count,
                    ClockSkewPercent = Math.Round((double)negativeCount / allDeltas.Count * 100, 1)
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to compute delivery latency");
                return new DeliveryLatencyMetrics();
            }
        }

        private async Task<CrashRateMetrics> ComputeCrashRate(
            List<AutopilotMonitor.Shared.Models.SessionSummary> sessions,
            ISessionRepository sessionRepo)
        {
            try
            {
                var metrics = new CrashRateMetrics();
                var exceptionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                var crashTasks = sessions.Select(async session =>
                {
                    try
                    {
                        var events = await sessionRepo.GetSessionEventsAsync(session.TenantId, session.SessionId);
                        return events
                            .Where(e => e.EventType == "agent_started" && e.Data != null)
                            .Select(e => e.Data)
                            .ToList();
                    }
                    catch { return new List<Dictionary<string, object>>(); }
                }).ToList();

                var crashResults = await Task.WhenAll(crashTasks);
                foreach (var startEvents in crashResults)
                {
                    foreach (var data in startEvents)
                    {
                        metrics.TotalStarts++;
                        var exitType = GetString(data, "previousExitType");
                        switch (exitType)
                        {
                            case "clean":
                                metrics.CleanExits++;
                                break;
                            case "exception_crash":
                                metrics.ExceptionCrashes++;
                                var exType = GetString(data, "previousCrashException");
                                if (!string.IsNullOrEmpty(exType))
                                {
                                    exceptionCounts.TryGetValue(exType, out var count);
                                    exceptionCounts[exType] = count + 1;
                                }
                                break;
                            case "hard_kill":
                                metrics.HardKills++;
                                break;
                            default:
                                metrics.FirstRuns++;
                                break;
                        }
                    }
                }

                var crashCount = metrics.ExceptionCrashes + metrics.HardKills;
                var nonFirstRuns = metrics.TotalStarts - metrics.FirstRuns;
                metrics.CrashRatePercent = nonFirstRuns > 0
                    ? Math.Round((double)crashCount / nonFirstRuns * 100, 1)
                    : 0;

                metrics.TopExceptions = exceptionCounts
                    .OrderByDescending(kv => kv.Value)
                    .Take(5)
                    .Select(kv => new CrashExceptionSummary { ExceptionType = kv.Key, Count = kv.Value })
                    .ToList();

                return metrics;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to compute crash rate");
                return new CrashRateMetrics();
            }
        }

        private static double Percentile(List<double> sortedValues, double percentile)
        {
            if (sortedValues.Count == 0) return 0;
            var index = (int)Math.Ceiling(percentile * sortedValues.Count) - 1;
            return sortedValues[Math.Max(0, Math.Min(index, sortedValues.Count - 1))];
        }

        private static double GetDouble(Dictionary<string, object> data, string key)
        {
            if (data.TryGetValue(key, out var value))
            {
                if (value is double d) return d;
                if (value is int i) return i;
                if (value is long l) return l;
                if (value is float f) return f;
                if (double.TryParse(value?.ToString(), out var parsed)) return parsed;
            }
            return 0;
        }

        private static string GetString(Dictionary<string, object> data, string key)
        {
            if (data.TryGetValue(key, out var value))
            {
                return value?.ToString() ?? string.Empty;
            }
            return string.Empty;
        }
    }

    // ── Response DTOs ────────────────────────────────────────────────────────────

    public class PlatformAgentMetricsResponse
    {
        public List<SessionAgentMetric> Sessions { get; set; } = new();
        public DeliveryLatencyMetrics? DeliveryLatency { get; set; }
        public CrashRateMetrics? CrashRate { get; set; }
        public DateTime ComputedAt { get; set; }
        public int ComputeDurationMs { get; set; }
        public bool FromCache { get; set; }
    }

    public class SessionAgentMetric
    {
        public string SessionId { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public string? DeviceName { get; set; }
        public string? Manufacturer { get; set; }
        public string? Model { get; set; }
        public string? StartedAt { get; set; }
        public string? Status { get; set; }
        public string? AgentVersion { get; set; }
        public int SnapshotCount { get; set; }
        public double TotalBytesUp { get; set; }
        public double TotalBytesDown { get; set; }
        public double TotalRequests { get; set; }
        public double AvgCpu { get; set; }
        public double MaxCpu { get; set; }
        public double AvgWorkingSet { get; set; }
        public double MaxWorkingSet { get; set; }
        public double AvgPrivateBytes { get; set; }
        public double AvgLatency { get; set; }
        public double AvgSpoolDepth { get; set; }
        public double MaxSpoolDepth { get; set; }
    }

    public class DeliveryLatencyMetrics
    {
        public double P50Ms { get; set; }
        public double P95Ms { get; set; }
        public double P99Ms { get; set; }
        public double AvgMs { get; set; }
        public int SampleCount { get; set; }
        public double ClockSkewPercent { get; set; }
    }

    public class CrashRateMetrics
    {
        public int TotalStarts { get; set; }
        public int CleanExits { get; set; }
        public int ExceptionCrashes { get; set; }
        public int HardKills { get; set; }
        public int FirstRuns { get; set; }
        public double CrashRatePercent { get; set; }
        public List<CrashExceptionSummary> TopExceptions { get; set; } = new();
    }

    public class CrashExceptionSummary
    {
        public string ExceptionType { get; set; } = string.Empty;
        public int Count { get; set; }
    }
}
