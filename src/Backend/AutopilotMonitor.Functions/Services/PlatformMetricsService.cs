using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Service for computing platform agent metrics (CPU, memory, network per session).
    /// Fetches sessions + their events server-side and caches the result for 5 minutes.
    /// </summary>
    public class PlatformMetricsService
    {
        private readonly TableStorageService _storageService;
        private readonly ILogger<PlatformMetricsService> _logger;

        // In-memory cache (same pattern as UsageMetricsService)
        private static PlatformAgentMetricsResponse? _cachedMetrics;
        private static DateTime _cacheExpiry = DateTime.MinValue;
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
        private static readonly object _cacheLock = new object();

        public PlatformMetricsService(
            TableStorageService storageService,
            ILogger<PlatformMetricsService> logger)
        {
            _storageService = storageService;
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
            var allSessions = await _storageService.GetAllSessionsAsync(maxResults: 100);

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
                    var events = await _storageService.GetSessionEventsAsync(session.TenantId, session.SessionId);
                    var snapshots = events
                        .Where(e => e.EventType == "agent_metrics_snapshot" && e.Data != null)
                        .Select(e => e.Data)
                        .ToList();

                    if (snapshots.Count == 0) return null;

                    var cpuValues = snapshots.Select(s => GetDouble(s, "agent_cpu_percent")).ToList();
                    var wsValues = snapshots.Select(s => GetDouble(s, "agent_working_set_mb")).ToList();
                    var pbValues = snapshots.Select(s => GetDouble(s, "agent_private_bytes_mb")).ToList();
                    var latValues = snapshots.Select(s => GetDouble(s, "net_avg_latency_ms")).Where(v => v > 0).ToList();

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
                        AvgLatency = latValues.Count > 0 ? latValues.Average() : 0
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

            return new PlatformAgentMetricsResponse
            {
                Sessions = sessionMetrics
            };
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
    }
}
