using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Flows;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Completion;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Periodic
{
    /// <summary>
    /// Measures the agent's own resource footprint: process CPU, memory, threads, handles,
    /// and HTTP network traffic. Emits agent_metrics_snapshot events via the standard event pipeline.
    /// No WMI, no PerformanceCounters — only Process properties and Interlocked counters.
    /// </summary>
    public class AgentSelfMetricsCollector : CollectorBase
    {
        private readonly string _agentVersion;
        private readonly NetworkMetrics _networkMetrics;
        private readonly EventSpool _spool;

        // Previous sample for delta calculations
        private TimeSpan _prevCpuTime;
        private DateTime _prevWallTime;
        private NetworkMetricsSnapshot _prevNetSnapshot;

        public AgentSelfMetricsCollector(
            string sessionId,
            string tenantId,
            Action<EnrollmentEvent> onEventCollected,
            NetworkMetrics networkMetrics,
            EventSpool spool,
            AgentLogger logger,
            string agentVersion = "unknown",
            int intervalSeconds = 60)
            : base(sessionId, tenantId, onEventCollected, logger, intervalSeconds)
        {
            _networkMetrics = networkMetrics ?? throw new ArgumentNullException(nameof(networkMetrics));
            _spool = spool;
            _agentVersion = string.IsNullOrWhiteSpace(agentVersion) ? "unknown" : agentVersion;
        }

        protected override void OnBeforeStart()
        {
            // Prime the baseline for delta calculations
            try
            {
                var proc = Process.GetCurrentProcess();
                _prevCpuTime = proc.TotalProcessorTime;
                _prevWallTime = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to prime CPU baseline: {ex.Message}");
                _prevCpuTime = TimeSpan.Zero;
                _prevWallTime = DateTime.UtcNow;
            }
            _prevNetSnapshot = _networkMetrics.GetSnapshot();
        }

        protected override void Collect()
        {
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
                Logger.Debug($"Process metrics read failed: {ex.Message}");
            }

            // --- Spool queue depth ---
            if (_spool != null)
            {
                try { data["spool_queue_depth"] = _spool.GetCount(); }
                catch { }
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
                Logger.Debug($"Network metrics read failed: {ex.Message}");
            }

            if (data.Count > 0)
            {
                EmitEvent(new EnrollmentEvent
                {
                    SessionId = SessionId,
                    TenantId = TenantId,
                    Timestamp = now,
                    EventType = "agent_metrics_snapshot",
                    Severity = EventSeverity.Debug,
                    Source = "AgentSelfMetricsCollector",
                    Phase = EnrollmentPhase.Unknown,
                    Message = $"Agent CPU: {(data.ContainsKey("agent_cpu_percent") ? data["agent_cpu_percent"] : "?")}%, " +
                              $"WS: {(data.ContainsKey("agent_working_set_mb") ? data["agent_working_set_mb"] : "?")} MB, " +
                              $"Net: {(data.ContainsKey("net_requests") ? data["net_requests"] : "?")} req, " +
                              $"\u2191{(data.ContainsKey("net_bytes_up") ? data["net_bytes_up"] : "?")} B, " +
                              $"\u2193{(data.ContainsKey("net_bytes_down") ? data["net_bytes_down"] : "?")} B",
                    Data = data
                });
            }
        }
    }
}
