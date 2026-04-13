using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.Core.Configuration;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Agent.Core.Monitoring.Analyzers;
using AutopilotMonitor.Agent.Core.Monitoring.Collectors;
using AutopilotMonitor.Agent.Core.Monitoring.Network;
using AutopilotMonitor.Agent.Core.Monitoring.Replay;
using AutopilotMonitor.Agent.Core.Monitoring.Tracking;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.Core.Monitoring.Core
{
    /// <summary>
    /// Manages collector lifecycle: start/stop, idle detection, restart after activity,
    /// max lifetime timer, gather rules, and analyzers.
    /// </summary>
    public class CollectorCoordinator : IDisposable
    {
        private readonly AgentConfiguration _configuration;
        private readonly AgentLogger _logger;
        private readonly string _agentVersion;
        private readonly Action<EnrollmentEvent> _emitEvent;
        private readonly Action<string, string, string, Dictionary<string, object>> _emitTraceEvent;
        private readonly BackendApiClient _apiClient;
        private readonly EventSpool _spool;
        private readonly RemoteConfigService _remoteConfigService;
        private readonly Func<bool> _isTerminalEventSeen;
        private readonly string _previousExitType;
        private readonly DateTime? _lastBootTimeUtc;
        private readonly DateTime _agentStartTimeUtc;

        // Core event collectors (always on)
        private EspAndHelloTracker _espAndHelloTracker;
        private LogReplayService _logReplay;

        // Optional collectors (toggled via remote config)
        private PerformanceCollector _performanceCollector;
        private AgentSelfMetricsCollector _agentSelfMetricsCollector;
        private DeliveryOptimizationCollector _deliveryOptimizationCollector;
        private StallProbeCollector _stallProbeCollector;

        // Smart enrollment tracking
        private EnrollmentTracker _enrollmentTracker;

        // Gather rules
        private GatherRuleExecutor _gatherRuleExecutor;

        // Desktop arrival / IME watcher / Network change
        private DesktopArrivalDetector _desktopArrivalDetector;
        private ImeProcessWatcher _imeProcessWatcher;
        private NetworkChangeDetector _networkChangeDetector;

        // Analyzers
        private readonly List<IAgentAnalyzer> _analyzers = new List<IAgentAnalyzer>();

        // Idle detection
        private DateTime _lastRealEventTime = DateTime.UtcNow;
        private bool _collectorsIdleStopped;
        private int _collectorIdleTimeoutMinutes = 15;
        private Timer _idleCheckTimer;

        // Max lifetime safety net
        private Timer _maxLifetimeTimer;

        public CollectorCoordinator(
            AgentConfiguration configuration,
            AgentLogger logger,
            string agentVersion,
            Action<EnrollmentEvent> emitEvent,
            Action<string, string, string, Dictionary<string, object>> emitTraceEvent,
            BackendApiClient apiClient,
            EventSpool spool,
            RemoteConfigService remoteConfigService,
            Func<bool> isTerminalEventSeen,
            string previousExitType,
            DateTime? lastBootTimeUtc,
            DateTime agentStartTimeUtc)
        {
            _configuration = configuration;
            _logger = logger;
            _agentVersion = agentVersion;
            _emitEvent = emitEvent;
            _emitTraceEvent = emitTraceEvent;
            _apiClient = apiClient;
            _spool = spool;
            _remoteConfigService = remoteConfigService;
            _isTerminalEventSeen = isTerminalEventSeen;
            _previousExitType = previousExitType;
            _lastBootTimeUtc = lastBootTimeUtc;
            _agentStartTimeUtc = agentStartTimeUtc;
        }

        /// <summary>
        /// Whether periodic collectors are currently paused due to idle timeout.
        /// </summary>
        public bool CollectorsIdleStopped => _collectorsIdleStopped;

        /// <summary>
        /// Called from EmitEvent when a non-periodic event is received.
        /// Updates idle tracking, resets stall probes, and restarts paused collectors.
        /// </summary>
        public void OnRealEventReceived()
        {
            _lastRealEventTime = DateTime.UtcNow;

            try { _stallProbeCollector?.ResetProbes(); }
            catch (Exception ex) { _logger.Verbose($"StallProbeCollector.ResetProbes failed: {ex.Message}"); }

            if (_collectorsIdleStopped)
            {
                RestartPeriodicCollectors();
            }
        }

        /// <summary>
        /// Cancels the max lifetime timer. Called when a terminal event is seen.
        /// </summary>
        public void CancelMaxLifetimeTimer()
        {
            _maxLifetimeTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>
        /// Notifies the gather rule executor of a phase change.
        /// </summary>
        public void OnPhaseChanged(EnrollmentPhase phase)
        {
            try { _gatherRuleExecutor?.OnPhaseChanged(phase); }
            catch (Exception ex) { _logger.Verbose($"GatherRuleExecutor.OnPhaseChanged failed: {ex.Message}"); }
        }

        /// <summary>
        /// Notifies the gather rule executor of an event type (for on_event triggers).
        /// </summary>
        public void OnEvent(string eventType)
        {
            try { _gatherRuleExecutor?.OnEvent(eventType); }
            catch (Exception ex) { _logger.Verbose($"GatherRuleExecutor.OnEvent('{eventType}') failed: {ex.Message}"); }
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
        /// Notifies the enrollment tracker that desktop has arrived.
        /// </summary>
        public void NotifyDesktopArrived()
        {
            _enrollmentTracker?.NotifyDesktopArrived();
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

        public void StartEventCollectors()
        {
            _logger.Info("Starting event collectors");

            try
            {
                var helloTimeout = _remoteConfigService?.CurrentConfig?.Collectors?.HelloWaitTimeoutSeconds
                    ?? _configuration.HelloWaitTimeoutSeconds;
                var modernDeploymentEnabled = _remoteConfigService?.CurrentConfig?.Collectors?.ModernDeploymentWatcherEnabled ?? true;
                var modernDeploymentLevelMax = _remoteConfigService?.CurrentConfig?.Collectors?.ModernDeploymentLogLevelMax ?? 3;
                var modernDeploymentBackfillEnabled = _remoteConfigService?.CurrentConfig?.Collectors?.ModernDeploymentBackfillEnabled ?? true;
                var modernDeploymentBackfillLookbackMinutes = _remoteConfigService?.CurrentConfig?.Collectors?.ModernDeploymentBackfillLookbackMinutes ?? 30;
                _espAndHelloTracker = new EspAndHelloTracker(
                    _configuration.SessionId,
                    _configuration.TenantId,
                    _emitEvent,
                    _logger,
                    helloTimeout,
                    modernDeploymentWatcherEnabled: modernDeploymentEnabled,
                    modernDeploymentLogLevelMax: modernDeploymentLevelMax,
                    modernDeploymentBackfillEnabled: modernDeploymentBackfillEnabled,
                    modernDeploymentBackfillLookbackMinutes: modernDeploymentBackfillLookbackMinutes,
                    stateDirectory: @"%ProgramData%\AutopilotMonitor\State"
                );
                _espAndHelloTracker.Start();

                if (!string.IsNullOrEmpty(_configuration.ReplayLogDir))
                {
                    _logger.Info($"Log replay mode enabled - starting log replay from: {_configuration.ReplayLogDir}");
                    var imeLogPatterns = _remoteConfigService?.CurrentConfig?.ImeLogPatterns;
                    _logReplay = new LogReplayService(
                        _configuration.SessionId,
                        _configuration.TenantId,
                        _emitEvent,
                        _logger,
                        _configuration.ReplayLogDir,
                        _configuration.ReplaySpeedFactor,
                        imeLogPatterns
                    );
                    _logReplay.Start();
                    _logger.Info("Log replay started");
                }

                _logger.Info("Core event collectors started successfully");
            }
            catch (Exception ex)
            {
                _logger.Error("Error starting event collectors", ex);
            }
        }

        public void StartOptionalCollectors()
        {
            var config = _remoteConfigService?.CurrentConfig;
            var collectors = config?.Collectors;

            _logger.Info("Starting collectors based on remote config");

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

                if (collectors?.StallProbeEnabled != false)
                {
                    var probeThresholds = collectors?.StallProbeThresholdsMinutes ?? new[] { 2, 15, 30, 60, 180 };
                    var probeTraceIndices = collectors?.StallProbeTraceIndices ?? new[] { 2, 3, 4 };
                    var probeSources = collectors?.StallProbeSources ?? new[]
                    {
                        "provisioning_registry", "diagnostics_registry", "eventlog", "appworkload_log"
                    };
                    var stalledAfterIndex = collectors?.SessionStalledAfterProbeIndex ?? 4;
                    _stallProbeCollector = new StallProbeCollector(
                        _configuration.SessionId,
                        _configuration.TenantId,
                        _emitEvent,
                        _logger,
                        probeThresholds,
                        probeTraceIndices,
                        probeSources,
                        stalledAfterIndex);
                    _logger.Info($"StallProbeCollector enabled: thresholds=[{string.Join(",", probeThresholds)}]min, traces=[{string.Join(",", probeTraceIndices)}], sources={probeSources.Length}");
                }
                else
                {
                    _logger.Info("StallProbeCollector disabled via config");
                }

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

                if (string.IsNullOrEmpty(_configuration.ReplayLogDir))
                {
                    var imeLogPatterns = config?.ImeLogPatterns;
                    var imeMatchLogPath = string.IsNullOrEmpty(_configuration.ImeMatchLogPath)
                        ? null
                        : Environment.ExpandEnvironmentVariables(_configuration.ImeMatchLogPath);

                    _enrollmentTracker = new EnrollmentTracker(
                        _configuration.SessionId,
                        _configuration.TenantId,
                        _emitEvent,
                        _logger,
                        imeLogPatterns,
                        _configuration.ImeLogPathOverride,
                        imeMatchLogPath: imeMatchLogPath,
                        espAndHelloTracker: _espAndHelloTracker,
                        isBootstrapMode: _configuration.UseBootstrapTokenAuth,
                        sendTraceEvents: _configuration.SendTraceEvents
                    );
                    _enrollmentTracker.Start();
                    _logger.Info("EnrollmentTracker started — listening for IME patterns");

                    if (_previousExitType == "reboot_kill" && _lastBootTimeUtc.HasValue)
                    {
                        _enrollmentTracker.NotifySystemRebootDetected();
                    }

                    if (_stallProbeCollector != null)
                    {
                        _stallProbeCollector.EspFailureDetected += (sender, failureType) =>
                        {
                            try
                            {
                                _enrollmentTracker?.ReportEspFailureFromExternalSource(failureType);
                            }
                            catch (Exception ex)
                            {
                                _logger.Error("Failed to forward stall probe failure to EnrollmentTracker", ex);
                            }
                        };
                    }
                }

                if (collectors?.EnableDeliveryOptimizationCollector != false && _enrollmentTracker != null)
                {
                    var doInterval = collectors?.DeliveryOptimizationIntervalSeconds ?? 3;
                    _deliveryOptimizationCollector = new DeliveryOptimizationCollector(
                        _configuration.SessionId,
                        _configuration.TenantId,
                        _emitEvent,
                        _logger,
                        doInterval,
                        () => _enrollmentTracker?.ImeTracker?.PackageStates,
                        app => _enrollmentTracker?.ImeTracker?.OnDoTelemetryReceived?.Invoke(app),
                        Environment.ExpandEnvironmentVariables(Constants.LogDirectory)
                    );
                    _deliveryOptimizationCollector.Start();
                    _logger.Info($"DeliveryOptimizationCollector started dormant (interval={doInterval}s, wakes on download)");

                    var existingHandler = _enrollmentTracker.ImeTracker.OnAppStateChanged;
                    var doCollector = _deliveryOptimizationCollector;
                    _enrollmentTracker.ImeTracker.OnAppStateChanged = (pkg, oldState, newState) =>
                    {
                        existingHandler?.Invoke(pkg, oldState, newState);
                        if (newState >= AppInstallationState.Downloading && newState <= AppInstallationState.Installing)
                            doCollector?.WakeUp();
                    };
                }

                _desktopArrivalDetector = new DesktopArrivalDetector(_logger);
                _desktopArrivalDetector.DesktopArrived += OnDesktopArrived;
                _desktopArrivalDetector.OnTraceEvent = (decision, reason, context) =>
                    _emitTraceEvent("DesktopArrivalDetector", decision, reason, context);
                _desktopArrivalDetector.Start();
                _logger.Info("DesktopArrivalDetector started — monitoring for real user desktop");

                _imeProcessWatcher = new ImeProcessWatcher(
                    _configuration.SessionId,
                    _configuration.TenantId,
                    _emitEvent,
                    _logger
                );
                _imeProcessWatcher.Start();
                _logger.Info("ImeProcessWatcher started — watching for IntuneManagementExtension.exe exit");

                _networkChangeDetector = new NetworkChangeDetector(
                    _configuration.SessionId,
                    _configuration.TenantId,
                    _emitEvent,
                    _logger,
                    _configuration.ApiBaseUrl
                );
                _networkChangeDetector.Start();
                _logger.Info("NetworkChangeDetector started — monitoring for network changes");

                var maxLifetimeMinutes = collectors?.AgentMaxLifetimeMinutes ?? _configuration.AgentMaxLifetimeMinutes;
                if (maxLifetimeMinutes > 0)
                {
                    _maxLifetimeTimer = new Timer(
                        MaxLifetimeTimerCallback,
                        null,
                        TimeSpan.FromMinutes(maxLifetimeMinutes),
                        Timeout.InfiniteTimeSpan);
                    _logger.Info($"Agent max lifetime timer armed: {maxLifetimeMinutes} min");
                }
                else
                {
                    _logger.Info("Agent max lifetime timer disabled (0)");
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Error starting optional collectors", ex);
            }
        }

        public void StopOptionalCollectors()
        {
            _idleCheckTimer?.Dispose();
            _idleCheckTimer = null;

            _maxLifetimeTimer?.Dispose();
            _maxLifetimeTimer = null;

            if (_desktopArrivalDetector != null)
            {
                _desktopArrivalDetector.DesktopArrived -= OnDesktopArrived;
                _desktopArrivalDetector.Dispose();
                _desktopArrivalDetector = null;
            }

            _imeProcessWatcher?.Dispose();
            _imeProcessWatcher = null;

            _networkChangeDetector?.Dispose();
            _networkChangeDetector = null;

            _performanceCollector?.Stop();
            _performanceCollector?.Dispose();
            _performanceCollector = null;

            _agentSelfMetricsCollector?.Stop();
            _agentSelfMetricsCollector?.Dispose();
            _agentSelfMetricsCollector = null;

            _deliveryOptimizationCollector?.Stop();
            _deliveryOptimizationCollector?.Dispose();
            _deliveryOptimizationCollector = null;

            _enrollmentTracker?.Stop();
            _enrollmentTracker?.Dispose();
            _enrollmentTracker = null;
        }

        public void StopEventCollectors()
        {
            _logger.Info("Stopping event collectors");

            try
            {
                _espAndHelloTracker?.Stop();
                _espAndHelloTracker?.Dispose();

                _logReplay?.Stop();
                _logReplay?.Dispose();

                StopOptionalCollectors();

                _gatherRuleExecutor?.Dispose();
                _gatherRuleExecutor = null;

                _logger.Info("Event collectors stopped");
            }
            catch (Exception ex)
            {
                _logger.Error("Error stopping event collectors", ex);
            }
        }

        public void StartGatherRuleExecutor()
        {
            var config = _remoteConfigService?.CurrentConfig;
            if (config?.GatherRules == null || config.GatherRules.Count == 0)
            {
                _logger.Info("No gather rules to execute");
                return;
            }

            try
            {
                _gatherRuleExecutor = new GatherRuleExecutor(
                    _configuration.SessionId,
                    _configuration.TenantId,
                    _emitEvent,
                    _logger,
                    _configuration.ImeLogPathOverride
                );
                _gatherRuleExecutor.UnrestrictedMode = _configuration.UnrestrictedMode;
                _gatherRuleExecutor.UpdateRules(config.GatherRules);
                _logger.Info($"GatherRuleExecutor started with {config.GatherRules.Count} rule(s)");
            }
            catch (Exception ex)
            {
                _logger.Error("Error starting gather rule executor", ex);
            }
        }

        public void InitializeAnalyzers()
        {
            _analyzers.Clear();

            var analyzerConfig = _remoteConfigService?.CurrentConfig?.Analyzers ?? new AnalyzerConfiguration();

            if (analyzerConfig.EnableLocalAdminAnalyzer)
            {
                _analyzers.Add(new LocalAdminAnalyzer(
                    _configuration.SessionId,
                    _configuration.TenantId,
                    _emitEvent,
                    _logger,
                    analyzerConfig.LocalAdminAllowedAccounts
                ));
                _logger.Info("LocalAdminAnalyzer registered");
            }
            else
            {
                _logger.Info("LocalAdminAnalyzer disabled by remote config");
            }

            if (analyzerConfig.EnableSoftwareInventoryAnalyzer)
            {
                _analyzers.Add(new SoftwareInventoryAnalyzer(
                    _configuration.SessionId,
                    _configuration.TenantId,
                    _emitEvent,
                    _logger
                ));
                _logger.Info("SoftwareInventoryAnalyzer registered");
            }
            else
            {
                _logger.Info("SoftwareInventoryAnalyzer disabled by remote config");
            }

            _logger.Info($"Analyzers initialized: {_analyzers.Count} active");
        }

        public void RunStartupAnalyzers()
        {
            if (_analyzers.Count == 0)
                return;

            var analyzers = new List<IAgentAnalyzer>(_analyzers);

            _logger.Info($"Scheduling {analyzers.Count} startup analyzer(s) on background thread");

            Task.Run(() =>
            {
                foreach (var analyzer in analyzers)
                {
                    try
                    {
                        analyzer.AnalyzeAtStartup();
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Analyzer {analyzer.Name} threw during startup", ex);
                    }
                }
            });
        }

        public void RunShutdownAnalyzers(int? whiteGlovePart = null)
        {
            if (_analyzers.Count == 0)
                return;

            _logger.Info($"Running {_analyzers.Count} shutdown analyzer(s) (whiteGlovePart={whiteGlovePart?.ToString() ?? "none"})");

            foreach (var analyzer in _analyzers)
            {
                try
                {
                    if (analyzer is SoftwareInventoryAnalyzer softwareAnalyzer)
                        softwareAnalyzer.AnalyzeAtShutdown(whiteGlovePart);
                    else
                        analyzer.AnalyzeAtShutdown();
                }
                catch (Exception ex)
                {
                    _logger.Error($"Analyzer {analyzer.Name} threw during shutdown", ex);
                }
            }
        }

        /// <summary>
        /// Updates the EnrollmentTracker's trace event setting.
        /// </summary>
        public void UpdateSendTraceEvents(bool sendTraceEvents)
        {
            _enrollmentTracker?.UpdateSendTraceEvents(sendTraceEvents);
        }

        private void MaxLifetimeTimerCallback(object state)
        {
            if (_isTerminalEventSeen())
            {
                _logger.Info("Max lifetime timer fired but terminal event already seen — ignoring");
                return;
            }

            var uptimeMinutes = (DateTime.UtcNow - _agentStartTimeUtc).TotalMinutes;
            _logger.Warning($"Agent max lifetime expired after {uptimeMinutes:F0} minutes — emitting enrollment_failed");

            _emitEvent(new EnrollmentEvent
            {
                SessionId = _configuration.SessionId,
                TenantId = _configuration.TenantId,
                EventType = "enrollment_failed",
                Severity = EventSeverity.Error,
                Source = "MonitoringService",
                Phase = EnrollmentPhase.Complete,
                Message = $"Agent max lifetime expired ({uptimeMinutes:F0} min) — enrollment did not complete in time",
                Data = new Dictionary<string, object>
                {
                    { "failureType", "agent_timeout" },
                    { "failureSource", "max_lifetime_timer" },
                    { "agentUptimeMinutes", Math.Round(uptimeMinutes, 1) }
                },
                ImmediateUpload = true
            });
        }

        private void OnDesktopArrived(object sender, EventArgs e)
        {
            if (_isTerminalEventSeen())
            {
                _logger.Debug("Desktop arrived but terminal event already seen — ignoring");
                return;
            }

            _emitEvent(new EnrollmentEvent
            {
                SessionId = _configuration.SessionId,
                TenantId = _configuration.TenantId,
                EventType = "desktop_arrived",
                Severity = EventSeverity.Info,
                Source = "DesktopArrivalDetector",
                Phase = EnrollmentPhase.Unknown,
                Message = "User desktop detected (explorer.exe under real user)",
                ImmediateUpload = true
            });

            _enrollmentTracker?.NotifyDesktopArrived();
        }

        private void IdleCheckCallback(object state)
        {
            var idleMinutes = (DateTime.UtcNow - _lastRealEventTime).TotalMinutes;

            try
            {
                _stallProbeCollector?.CheckAndRunProbes(idleMinutes);
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
            _maxLifetimeTimer?.Dispose();
            _espAndHelloTracker?.Dispose();
            _logReplay?.Dispose();
            _performanceCollector?.Dispose();
            _agentSelfMetricsCollector?.Dispose();
            _deliveryOptimizationCollector?.Dispose();
            _enrollmentTracker?.Dispose();
            _networkChangeDetector?.Dispose();
            _gatherRuleExecutor?.Dispose();
            if (_desktopArrivalDetector != null)
            {
                _desktopArrivalDetector.DesktopArrived -= OnDesktopArrived;
                _desktopArrivalDetector.Dispose();
            }
            _imeProcessWatcher?.Dispose();
        }
    }
}
