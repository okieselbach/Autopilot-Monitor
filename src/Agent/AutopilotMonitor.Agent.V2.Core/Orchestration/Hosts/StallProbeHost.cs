#nullable enable
using System;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.DecisionCore.Engine;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    internal sealed class StallProbeHost : ICollectorHost
    {
        public string Name => "StallProbeCollector";

        private static readonly TimeSpan IdleTickInterval = TimeSpan.FromSeconds(60);

        private readonly StallProbeCollector _collector;
        private readonly StallProbeCollectorAdapter _adapter;
        private readonly AgentLogger _logger;
        private Timer? _tickTimer;
        private readonly DateTime _startedAtUtc;
        private int _disposed;

        public StallProbeHost(
            string sessionId,
            string tenantId,
            AgentLogger logger,
            ISignalIngressSink ingress,
            IClock clock,
            int[]? thresholdsMinutes,
            int[]? traceIndices,
            string[]? sources,
            int sessionStalledAfterProbeIndex,
            int[]? harmlessModernDeploymentEventIds)
        {
            if (ingress == null) throw new ArgumentNullException(nameof(ingress));
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            _logger = logger;
            var post = new InformationalEventPost(ingress, clock);
            _collector = new StallProbeCollector(
                sessionId: sessionId,
                tenantId: tenantId,
                post: post,
                logger: logger,
                thresholdsMinutes: thresholdsMinutes ?? new[] { 2, 15, 30, 60, 180 },
                traceIndices: traceIndices ?? new[] { 2 },
                sources: sources ?? new[] { "provisioning_registry", "diagnostics_registry", "eventlog", "appworkload_log" },
                sessionStalledAfterProbeIndex: sessionStalledAfterProbeIndex,
                harmlessModernDeploymentEventIds: harmlessModernDeploymentEventIds);

            _adapter = new StallProbeCollectorAdapter(_collector, ingress, clock);
            _startedAtUtc = DateTime.UtcNow;
        }

        public void Start()
        {
            // The collector has no timer of its own — we drive it with a 60-s idle tick.
            _tickTimer = new Timer(
                _ => SafeTick(),
                state: null,
                dueTime: IdleTickInterval,
                period: IdleTickInterval);
            _logger.Info($"StallProbeHost: started (tick every {IdleTickInterval.TotalSeconds}s).");
        }

        public void Stop()
        {
            try
            {
                _tickTimer?.Dispose();
                _tickTimer = null;
            }
            catch (Exception ex) { _logger.Warning($"StallProbeHost: timer dispose failed: {ex.Message}"); }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            Stop();
            try { _adapter.Dispose(); } catch { }
        }

        private void SafeTick()
        {
            try
            {
                var idleMinutes = (DateTime.UtcNow - _startedAtUtc).TotalMinutes;
                _collector.CheckAndRunProbes(idleMinutes);
            }
            catch (Exception ex)
            {
                _logger.Error("StallProbeHost: tick failed.", ex);
            }
        }
    }
}
