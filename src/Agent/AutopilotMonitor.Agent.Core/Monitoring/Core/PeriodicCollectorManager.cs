using System;
using System.Collections.Generic;
using System.Threading;
using AutopilotMonitor.Agent.Core.Configuration;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Agent.Core.Monitoring.Collectors;
using AutopilotMonitor.Agent.Core.Monitoring.Network;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.Core.Monitoring.Core
{
    /// <summary>
    /// Manages periodic collectors (PerformanceCollector, AgentSelfMetricsCollector) and their
    /// idle lifecycle: stops them after inactivity, restarts when real enrollment activity resumes.
    /// </summary>
    public class PeriodicCollectorManager : IDisposable
    {
        private readonly AgentConfiguration _configuration;
        private readonly AgentLogger _logger;
        private readonly string _agentVersion;
        private readonly Action<EnrollmentEvent> _emitEvent;
        private readonly BackendApiClient _apiClient;
        private readonly EventSpool _spool;
        private readonly RemoteConfigService _remoteConfigService;

        private PerformanceCollector _performanceCollector;
        private AgentSelfMetricsCollector _agentSelfMetricsCollector;

        private DateTime _lastRealEventTime = DateTime.UtcNow;
        private bool _collectorsIdleStopped;
        private int _collectorIdleTimeoutMinutes = 15;
        private Timer _idleCheckTimer;

        /// <summary>
        /// Optional reference to the StallProbeCollector for resetting/running probes
        /// during idle checks. Set after creation in StartOptionalCollectors.
        /// </summary>
        public StallProbeCollector StallProbeCollector { get; set; }

        public PeriodicCollectorManager(
            AgentConfiguration configuration,
            AgentLogger logger,
            string agentVersion,
            Action<EnrollmentEvent> emitEvent,
            BackendApiClient apiClient,
            EventSpool spool,
            RemoteConfigService remoteConfigService)
        {
            _configuration = configuration;
            _logger = logger;
            _agentVersion = agentVersion;
            _emitEvent = emitEvent;
            _apiClient = apiClient;
            _spool = spool;
            _remoteConfigService = remoteConfigService;
        }

        /// <summary>
        /// Whether periodic collectors are currently paused due to idle timeout.
        /// </summary>
        public bool CollectorsIdleStopped => _collectorsIdleStopped;

        /// <summary>
        /// Starts the performance and metrics collectors and arms the idle check timer.
        /// Called from CollectorCoordinator.StartOptionalCollectors.
        /// </summary>
        public void Start()
        {
            var config = _remoteConfigService?.CurrentConfig;
            var collectors = config?.Collectors;

            var perfInterval = collectors?.PerformanceIntervalSeconds ?? 60;
            _performanceCollector = new PerformanceCollector(
                _configuration.SessionId,
                _configuration.TenantId,
                _emitEvent,
                _logger,
                perfInterval
            );
            _performanceCollector.Start();
            _logger.Info($"PerformanceCollector started (interval={perfInterval}s)");

            if (collectors?.EnableAgentSelfMetrics != false)
            {
                var selfMetricsInterval = collectors?.AgentSelfMetricsIntervalSeconds ?? 60;
                _agentSelfMetricsCollector = new AgentSelfMetricsCollector(
                    _configuration.SessionId,
                    _configuration.TenantId,
                    _emitEvent,
                    _apiClient.NetworkMetrics,
                    _spool,
                    _logger,
                    _agentVersion,
                    selfMetricsInterval
                );
                _agentSelfMetricsCollector.Start();
                _logger.Info($"AgentSelfMetricsCollector started (interval={selfMetricsInterval}s)");
            }

            _collectorIdleTimeoutMinutes = collectors?.CollectorIdleTimeoutMinutes ?? 15;
            _lastRealEventTime = DateTime.UtcNow;
            _collectorsIdleStopped = false;

            _idleCheckTimer = new Timer(
                IdleCheckCallback,
                null,
                TimeSpan.FromSeconds(60),
                TimeSpan.FromSeconds(60)
            );
            if (_collectorIdleTimeoutMinutes > 0)
                _logger.Info($"Collector idle timeout enabled: {_collectorIdleTimeoutMinutes} min");
            else
                _logger.Info("Collector idle timeout disabled (0)");
        }

        /// <summary>
        /// Called when a non-periodic event is received. Updates idle tracking,
        /// resets stall probes, and restarts paused collectors.
        /// </summary>
        public void OnRealEventReceived()
        {
            _lastRealEventTime = DateTime.UtcNow;

            try { StallProbeCollector?.ResetProbes(); }
            catch (Exception ex) { _logger.Verbose($"StallProbeCollector.ResetProbes failed: {ex.Message}"); }

            if (_collectorsIdleStopped)
            {
                RestartPeriodicCollectors();
            }
        }

        /// <summary>
        /// Stops just the performance collector (used during WhiteGlove completion).
        /// </summary>
        public void StopPerformanceCollectorOnly()
        {
            _performanceCollector?.Stop();
            _performanceCollector?.Dispose();
            _performanceCollector = null;
        }

        /// <summary>
        /// Stops all periodic collectors and the idle check timer.
        /// </summary>
        public void Stop()
        {
            _idleCheckTimer?.Dispose();
            _idleCheckTimer = null;

            _performanceCollector?.Stop();
            _performanceCollector?.Dispose();
            _performanceCollector = null;

            _agentSelfMetricsCollector?.Stop();
            _agentSelfMetricsCollector?.Dispose();
            _agentSelfMetricsCollector = null;
        }

        /// <summary>
        /// Returns true for events generated by periodic timers (not real enrollment activity).
        /// </summary>
        public static bool IsPeriodicEvent(string eventType)
        {
            return eventType == "performance_snapshot" ||
                   eventType == "agent_metrics_snapshot" ||
                   eventType == "performance_collector_stopped" ||
                   eventType == "agent_metrics_collector_stopped" ||
                   eventType == Constants.EventTypes.StallProbeCheck ||
                   eventType == Constants.EventTypes.StallProbeResult ||
                   eventType == Constants.EventTypes.SessionStalled;
        }

        private void IdleCheckCallback(object state)
        {
            var idleMinutes = (DateTime.UtcNow - _lastRealEventTime).TotalMinutes;

            try
            {
                StallProbeCollector?.CheckAndRunProbes(idleMinutes);
            }
            catch (Exception ex)
            {
                _logger.Error("StallProbeCollector.CheckAndRunProbes threw", ex);
            }

            if (_collectorsIdleStopped)
                return;

            if (idleMinutes < _collectorIdleTimeoutMinutes)
                return;

            _logger.Info($"Collector idle timeout reached ({_collectorIdleTimeoutMinutes} min, idle for {idleMinutes:F0} min) — stopping periodic collectors");

            if (_performanceCollector != null)
            {
                _performanceCollector.Stop();
                _performanceCollector.Dispose();
                _performanceCollector = null;

                _emitEvent(new EnrollmentEvent
                {
                    SessionId = _configuration.SessionId,
                    TenantId = _configuration.TenantId,
                    Timestamp = DateTime.UtcNow,
                    EventType = "performance_collector_stopped",
                    Severity = EventSeverity.Info,
                    Source = "MonitoringService",
                    Phase = EnrollmentPhase.Unknown,
                    Message = $"Performance collector stopped after {_collectorIdleTimeoutMinutes} min idle (no real enrollment activity)",
                    Data = new Dictionary<string, object>
                    {
                        { "reason", "idle_timeout" },
                        { "idleTimeoutMinutes", _collectorIdleTimeoutMinutes },
                        { "idleMinutes", Math.Round(idleMinutes, 1) }
                    }
                });
            }

            if (_agentSelfMetricsCollector != null)
            {
                _agentSelfMetricsCollector.Stop();
                _agentSelfMetricsCollector.Dispose();
                _agentSelfMetricsCollector = null;

                _emitEvent(new EnrollmentEvent
                {
                    SessionId = _configuration.SessionId,
                    TenantId = _configuration.TenantId,
                    Timestamp = DateTime.UtcNow,
                    EventType = "agent_metrics_collector_stopped",
                    Severity = EventSeverity.Info,
                    Source = "MonitoringService",
                    Phase = EnrollmentPhase.Unknown,
                    Message = $"AgentSelfMetrics collector stopped after {_collectorIdleTimeoutMinutes} min idle (no real enrollment activity)",
                    Data = new Dictionary<string, object>
                    {
                        { "reason", "idle_timeout" },
                        { "idleTimeoutMinutes", _collectorIdleTimeoutMinutes },
                        { "idleMinutes", Math.Round(idleMinutes, 1) }
                    }
                });
            }

            _collectorsIdleStopped = true;
        }

        private void RestartPeriodicCollectors()
        {
            var config = _remoteConfigService?.CurrentConfig;
            var collectors = config?.Collectors;

            _logger.Info("Restarting periodic collectors — new enrollment activity detected after idle stop");

            try
            {
                var perfInterval = collectors?.PerformanceIntervalSeconds ?? 60;
                _performanceCollector = new PerformanceCollector(
                    _configuration.SessionId,
                    _configuration.TenantId,
                    _emitEvent,
                    _logger,
                    perfInterval
                );
                _performanceCollector.Start();

                if (collectors?.EnableAgentSelfMetrics != false)
                {
                    var selfMetricsInterval = collectors?.AgentSelfMetricsIntervalSeconds ?? 60;
                    _agentSelfMetricsCollector = new AgentSelfMetricsCollector(
                        _configuration.SessionId,
                        _configuration.TenantId,
                        _emitEvent,
                        _apiClient.NetworkMetrics,
                        _spool,
                        _logger,
                        _agentVersion,
                        selfMetricsInterval
                    );
                    _agentSelfMetricsCollector.Start();
                }

                _lastRealEventTime = DateTime.UtcNow;
                _collectorsIdleStopped = false;
                if (_collectorIdleTimeoutMinutes > 0)
                {
                    _idleCheckTimer?.Change(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
                }

                _logger.Info("Periodic collectors restarted successfully");
            }
            catch (Exception ex)
            {
                _logger.Error("Error restarting periodic collectors", ex);
            }
        }

        public void Dispose()
        {
            _idleCheckTimer?.Dispose();
            _performanceCollector?.Dispose();
            _agentSelfMetricsCollector?.Dispose();
        }
    }
}
