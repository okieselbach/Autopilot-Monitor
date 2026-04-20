using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Periodic;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Flows;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Completion;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime
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
        private DeliveryOptimizationCollector _deliveryOptimizationCollector;
        private StallProbeCollector _stallProbeCollector;

        // Periodic collectors + idle management
        private PeriodicCollectorManager _periodicManager;

        // Smart enrollment tracking
        private EnrollmentTracker _enrollmentTracker;

        // Gather rules
        private GatherRuleExecutor _gatherRuleExecutor;

        // Desktop arrival / IME watcher / Network change
        private DesktopArrivalDetector _desktopArrivalDetector;
        private AadJoinWatcher _aadJoinWatcher;
        private ImeProcessWatcher _imeProcessWatcher;
        private NetworkChangeDetector _networkChangeDetector;

        // Analyzers
        private AnalyzerManager _analyzerManager;

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
        public bool CollectorsIdleStopped => _periodicManager?.CollectorsIdleStopped ?? false;

        /// <summary>
        /// Called from EmitEvent when a non-periodic event is received.
        /// Updates idle tracking, resets stall probes, and restarts paused collectors.
        /// </summary>
        public void OnRealEventReceived()
        {
            _periodicManager?.OnRealEventReceived();
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
            _periodicManager?.StopPerformanceCollectorOnly();
        }

        /// <summary>
        /// Notifies the enrollment tracker that desktop has arrived.
        /// </summary>
        public void NotifyDesktopArrived()
        {
            _enrollmentTracker?.NotifyDesktopArrived();
        }

        /// Backend validator verdict cached when RegisterSession completes before the
        /// EnrollmentTracker is instantiated. Applied to the tracker in StartEventCollectors().
        private ValidatorType _pendingBackendValidator = ValidatorType.Unknown;

        /// <summary>
        /// Forwards the backend's authoritative validator verdict to the enrollment tracker
        /// so it can reconcile with its registry-based enrollment-type detection.
        /// If the tracker does not yet exist (RegisterSession runs before tracker start),
        /// the verdict is cached and applied when the tracker is created.
        /// </summary>
        public void ReconcileWithBackendValidator(ValidatorType validatedBy)
        {
            if (_enrollmentTracker != null)
            {
                _enrollmentTracker.ReconcileWithBackendValidator(validatedBy);
            }
            else
            {
                _pendingBackendValidator = validatedBy;
            }
        }

        /// <summary>
        /// Returns true for events generated by periodic timers (not real enrollment activity).
        /// </summary>
        public static bool IsPeriodicEvent(string eventType)
        {
            return PeriodicCollectorManager.IsPeriodicEvent(eventType);
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
                var modernDeploymentHarmlessEventIds = _remoteConfigService?.CurrentConfig?.Collectors?.ModernDeploymentHarmlessEventIds;
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
                    stateDirectory: @"%ProgramData%\AutopilotMonitor\State",
                    modernDeploymentHarmlessEventIds: modernDeploymentHarmlessEventIds
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
                _periodicManager = new PeriodicCollectorManager(
                    _configuration, _logger, _agentVersion,
                    _emitEvent, _apiClient, _spool, _remoteConfigService);
                _periodicManager.Start();

                if (collectors?.StallProbeEnabled != false)
                {
                    var probeThresholds = collectors?.StallProbeThresholdsMinutes ?? new[] { 2, 15, 30, 60, 180 };
                    var probeTraceIndices = collectors?.StallProbeTraceIndices ?? new[] { 2, 3, 4 };
                    var probeSources = collectors?.StallProbeSources ?? new[]
                    {
                        "provisioning_registry", "diagnostics_registry", "eventlog", "appworkload_log"
                    };
                    var stalledAfterIndex = collectors?.SessionStalledAfterProbeIndex ?? 4;
                    var harmlessMdmIds = collectors?.ModernDeploymentHarmlessEventIds ?? new[] { 100, 1005 };
                    _stallProbeCollector = new StallProbeCollector(
                        _configuration.SessionId,
                        _configuration.TenantId,
                        _emitEvent,
                        _logger,
                        probeThresholds,
                        probeTraceIndices,
                        probeSources,
                        stalledAfterIndex,
                        harmlessMdmIds);
                    _logger.Info($"StallProbeCollector enabled: thresholds=[{string.Join(",", probeThresholds)}]min, traces=[{string.Join(",", probeTraceIndices)}], sources={probeSources.Length}");
                }
                else
                {
                    _logger.Info("StallProbeCollector disabled via config");
                }

                _periodicManager.StallProbeCollector = _stallProbeCollector;

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

                    if (_pendingBackendValidator != ValidatorType.Unknown)
                    {
                        _enrollmentTracker.ReconcileWithBackendValidator(_pendingBackendValidator);
                        _pendingBackendValidator = ValidatorType.Unknown;
                    }

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

                _aadJoinWatcher = new AadJoinWatcher(_logger);
                _aadJoinWatcher.PlaceholderUserDetected += OnAadPlaceholderUserDetected;
                _aadJoinWatcher.AadUserJoined += OnAadUserJoined;
                _aadJoinWatcher.Start();
                _logger.Info("AadJoinWatcher started — monitoring JoinInfo registry for late AAD user");

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
            _periodicManager?.Stop();

            _maxLifetimeTimer?.Dispose();
            _maxLifetimeTimer = null;

            if (_desktopArrivalDetector != null)
            {
                _desktopArrivalDetector.DesktopArrived -= OnDesktopArrived;
                _desktopArrivalDetector.Dispose();
                _desktopArrivalDetector = null;
            }

            if (_aadJoinWatcher != null)
            {
                _aadJoinWatcher.PlaceholderUserDetected -= OnAadPlaceholderUserDetected;
                _aadJoinWatcher.AadUserJoined -= OnAadUserJoined;
                _aadJoinWatcher.Dispose();
                _aadJoinWatcher = null;
            }

            _imeProcessWatcher?.Dispose();
            _imeProcessWatcher = null;

            _networkChangeDetector?.Dispose();
            _networkChangeDetector = null;

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
            _analyzerManager = new AnalyzerManager(_configuration, _logger, _emitEvent, _remoteConfigService);
            _analyzerManager.Initialize();
        }

        public void RunStartupAnalyzers()
        {
            _analyzerManager?.RunStartup();
        }

        public void RunShutdownAnalyzers(int? whiteGlovePart = null)
        {
            _analyzerManager?.RunShutdown(whiteGlovePart);
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

        public void Dispose()
        {
            _periodicManager?.Dispose();
            _maxLifetimeTimer?.Dispose();
            _espAndHelloTracker?.Dispose();
            _logReplay?.Dispose();
            _deliveryOptimizationCollector?.Dispose();
            _enrollmentTracker?.Dispose();
            _networkChangeDetector?.Dispose();
            _gatherRuleExecutor?.Dispose();
            if (_desktopArrivalDetector != null)
            {
                _desktopArrivalDetector.DesktopArrived -= OnDesktopArrived;
                _desktopArrivalDetector.Dispose();
            }
            if (_aadJoinWatcher != null)
            {
                _aadJoinWatcher.PlaceholderUserDetected -= OnAadPlaceholderUserDetected;
                _aadJoinWatcher.AadUserJoined -= OnAadUserJoined;
                _aadJoinWatcher.Dispose();
            }
            _imeProcessWatcher?.Dispose();
        }

        private void OnAadPlaceholderUserDetected(object sender, AadPlaceholderUserDetectedEventArgs e)
        {
            try
            {
                _enrollmentTracker?.NotifyAadPlaceholderUserDetected(e.UserEmail);
            }
            catch (Exception ex)
            {
                _logger.Error("CollectorCoordinator: OnAadPlaceholderUserDetected failed", ex);
            }
        }

        private void OnAadUserJoined(object sender, AadUserJoinedEventArgs e)
        {
            try
            {
                _enrollmentTracker?.NotifyAadUserJoinedLate(e.UserEmail);
            }
            catch (Exception ex)
            {
                _logger.Error("CollectorCoordinator: OnAadUserJoined failed", ex);
            }
        }
    }
}
