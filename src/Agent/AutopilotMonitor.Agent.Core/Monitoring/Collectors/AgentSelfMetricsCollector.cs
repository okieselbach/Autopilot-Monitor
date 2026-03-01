using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Agent.Core.Monitoring.Network;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.Core.Monitoring.Collectors
{
    /// <summary>
    /// Measures the agent's own resource footprint: process CPU, memory, threads, handles,
    /// and HTTP network traffic. Emits agent_metrics_snapshot events via the standard event pipeline.
    /// No WMI, no PerformanceCounters — only Process properties and Interlocked counters.
    /// </summary>
    public class AgentSelfMetricsCollector : IDisposable
    {
        private readonly AgentLogger _logger;
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly string _agentVersion;
        private readonly Action<EnrollmentEvent> _onEventCollected;
        private readonly NetworkMetrics _networkMetrics;
        private readonly int _intervalSeconds;
        private readonly int _maxDurationHours;

        private Timer _pollTimer;
        private DateTime _startedAt;

        // Previous sample for delta calculations
        private TimeSpan _prevCpuTime;
        private DateTime _prevWallTime;
        private NetworkMetricsSnapshot _prevNetSnapshot;

        public AgentSelfMetricsCollector(
            string sessionId,
            string tenantId,
            Action<EnrollmentEvent> onEventCollected,
            NetworkMetrics networkMetrics,
            AgentLogger logger,
            string agentVersion = "unknown",
            int intervalSeconds = 60,
            int maxDurationHours = 4)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _onEventCollected = onEventCollected ?? throw new ArgumentNullException(nameof(onEventCollected));
            _networkMetrics = networkMetrics ?? throw new ArgumentNullException(nameof(networkMetrics));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _agentVersion = string.IsNullOrWhiteSpace(agentVersion) ? "unknown" : agentVersion;
            _intervalSeconds = intervalSeconds;
            _maxDurationHours = maxDurationHours;
        }

        public void Start()
        {
            var limitInfo = _maxDurationHours > 0 ? $", max duration: {_maxDurationHours}h" : ", no duration limit";
            _logger.Info($"Starting AgentSelfMetrics collector (interval: {_intervalSeconds}s{limitInfo})");

            _startedAt = DateTime.UtcNow;

            // Prime the baseline for delta calculations
            try
            {
                var proc = Process.GetCurrentProcess();
                _prevCpuTime = proc.TotalProcessorTime;
                _prevWallTime = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to prime CPU baseline: {ex.Message}");
                _prevCpuTime = TimeSpan.Zero;
                _prevWallTime = DateTime.UtcNow;
            }
            _prevNetSnapshot = _networkMetrics.GetSnapshot();

            _pollTimer = new Timer(
                _ => CollectMetrics(),
                null,
                TimeSpan.FromSeconds(_intervalSeconds), // first tick after one full interval
                TimeSpan.FromSeconds(_intervalSeconds)
            );
        }

        public void Stop()
        {
            _logger.Info("Stopping AgentSelfMetrics collector");
            _pollTimer?.Dispose();
            _pollTimer = null;
        }

        private void CollectMetrics()
        {
            try
            {
                // Max duration guard — same pattern as PerformanceCollector
                if (_maxDurationHours > 0 && (DateTime.UtcNow - _startedAt).TotalHours >= _maxDurationHours)
                {
                    _logger.Info($"AgentSelfMetrics collector max duration reached ({_maxDurationHours}h) — stopping");
                    _onEventCollected(new EnrollmentEvent
                    {
                        SessionId = _sessionId,
                        TenantId = _tenantId,
                        Timestamp = DateTime.UtcNow,
                        EventType = "agent_metrics_collector_stopped",
                        Severity = EventSeverity.Info,
                        Source = "AgentSelfMetricsCollector",
                        Phase = EnrollmentPhase.Unknown,
                        Message = $"AgentSelfMetrics collector stopped after {_maxDurationHours}h (max duration policy)",
                        Data = new Dictionary<string, object>
                        {
                            { "reason", "max_duration_reached" },
                            { "maxDurationHours", _maxDurationHours }
                        }
                    });
                    Stop();
                    return;
                }

                var data = new Dictionary<string, object>
                {
                    { "agent_version", _agentVersion }
                };
                var now = DateTime.UtcNow;

                // --- Process metrics (no WMI, no PerformanceCounter) ---
                try
                {
                    var proc = Process.GetCurrentProcess();
                    proc.Refresh(); // ensure fresh values

                    // CPU %: (delta CPU time) / (delta wall time) / cores * 100
                    var currentCpuTime = proc.TotalProcessorTime;
                    var cpuDelta = currentCpuTime - _prevCpuTime;
                    var wallDelta = now - _prevWallTime;

                    if (wallDelta.TotalMilliseconds > 0)
                    {
                        var cpuPercent = cpuDelta.TotalMilliseconds / wallDelta.TotalMilliseconds
                                         / Environment.ProcessorCount * 100.0;
                        data["agent_cpu_percent"] = Math.Round(cpuPercent, 2);
                    }

                    _prevCpuTime = currentCpuTime;
                    _prevWallTime = now;

                    // Memory
                    data["agent_working_set_mb"] = Math.Round(proc.WorkingSet64 / (1024.0 * 1024), 1);
                    data["agent_private_bytes_mb"] = Math.Round(proc.PrivateMemorySize64 / (1024.0 * 1024), 1);

                    // Threads & handles
                    data["agent_thread_count"] = proc.Threads.Count;
                    data["agent_handle_count"] = proc.HandleCount;
                }
                catch (Exception ex)
                {
                    _logger.Debug($"Process metrics read failed: {ex.Message}");
                }

                // --- Network delta ---
                try
                {
                    var currentNet = _networkMetrics.GetSnapshot();
                    var delta = currentNet.DeltaFrom(_prevNetSnapshot);
                    _prevNetSnapshot = currentNet;

                    data["net_requests"] = delta.Requests;
                    data["net_failures"] = delta.Failures;
                    data["net_bytes_up"] = delta.BytesUp;
                    data["net_bytes_down"] = delta.BytesDown;
                    data["net_avg_latency_ms"] = Math.Round(delta.AvgLatencyMs, 1);

                    // Cumulative totals for easy "total cost of this session" view
                    data["net_total_bytes_up"] = currentNet.TotalBytesUp;
                    data["net_total_bytes_down"] = currentNet.TotalBytesDown;
                    data["net_total_requests"] = currentNet.RequestCount;
                }
                catch (Exception ex)
                {
                    _logger.Debug($"Network metrics read failed: {ex.Message}");
                }

                if (data.Count > 0)
                {
                    _onEventCollected(new EnrollmentEvent
                    {
                        SessionId = _sessionId,
                        TenantId = _tenantId,
                        Timestamp = now,
                        EventType = "agent_metrics_snapshot",
                        Severity = EventSeverity.Debug,
                        Source = "AgentSelfMetricsCollector",
                        Phase = EnrollmentPhase.Unknown,
                        Message = $"Agent CPU: {(data.ContainsKey("agent_cpu_percent") ? data["agent_cpu_percent"] : "?")}%, " +
                                  $"WS: {(data.ContainsKey("agent_working_set_mb") ? data["agent_working_set_mb"] : "?")} MB, " +
                                  $"Net: {(data.ContainsKey("net_requests") ? data["net_requests"] : "?")} req, " +
                                  $"↑{(data.ContainsKey("net_bytes_up") ? data["net_bytes_up"] : "?")} B, " +
                                  $"↓{(data.ContainsKey("net_bytes_down") ? data["net_bytes_down"] : "?")} B",
                        Data = data
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"AgentSelfMetrics collection failed: {ex.Message}");
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
