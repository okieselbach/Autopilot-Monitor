using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    /// Partial: Collector lifecycle management (start/stop optional collectors, idle timeout,
    /// max lifetime timer, gather rules, analyzers, session registration).
    /// </summary>
    public partial class MonitoringService
    {
        private void StartOptionalCollectors()
        {
            var config = _remoteConfigService?.CurrentConfig;
            var collectors = config?.Collectors;

            _logger.Info("Starting collectors based on remote config");

            try
            {
                // PerformanceCollector is always on (feeds UI chart)
                var perfInterval = collectors?.PerformanceIntervalSeconds ?? 60;
                _performanceCollector = new PerformanceCollector(
                    _configuration.SessionId,
                    _configuration.TenantId,
                    EmitEvent,
                    _logger,
                    perfInterval
                );
                _performanceCollector.Start();
                _logger.Info($"PerformanceCollector started (interval={perfInterval}s)");

                // AgentSelfMetricsCollector: measures the agent's own CPU, memory, and network footprint
                if (collectors?.EnableAgentSelfMetrics != false)
                {
                    var selfMetricsInterval = collectors?.AgentSelfMetricsIntervalSeconds ?? 60;
                    _agentSelfMetricsCollector = new AgentSelfMetricsCollector(
                        _configuration.SessionId,
                        _configuration.TenantId,
                        EmitEvent,
                        _apiClient.NetworkMetrics,
                        _logger,
                        _agentVersion,
                        selfMetricsInterval
                    );
                    _agentSelfMetricsCollector.Start();
                    _logger.Info($"AgentSelfMetricsCollector started (interval={selfMetricsInterval}s)");
                }

                // Collector idle timeout — stop periodic collectors when enrollment goes quiet
                _collectorIdleTimeoutMinutes = collectors?.CollectorIdleTimeoutMinutes ?? 15;
                _lastRealEventTime = DateTime.UtcNow;
                _collectorsIdleStopped = false;
                if (_collectorIdleTimeoutMinutes > 0)
                {
                    _idleCheckTimer = new Timer(
                        IdleCheckCallback,
                        null,
                        TimeSpan.FromSeconds(60),
                        TimeSpan.FromSeconds(60)
                    );
                    _logger.Info($"Collector idle timeout enabled: {_collectorIdleTimeoutMinutes} min");
                }
                else
                {
                    _logger.Info("Collector idle timeout disabled (0)");
                }

                // EnrollmentTracker: smart enrollment tracking with IME log parsing
                // (replaces DownloadProgressCollector + EspUiStateCollector)
                // Skip when log replay is active - LogReplayService starts its own EnrollmentTracker
                if (string.IsNullOrEmpty(_configuration.ReplayLogDir))
                {
                    var imeLogPatterns = config?.ImeLogPatterns;
                    var imeMatchLogPath = string.IsNullOrEmpty(_configuration.ImeMatchLogPath)
                        ? null
                        : Environment.ExpandEnvironmentVariables(_configuration.ImeMatchLogPath);

                    _enrollmentTracker = new EnrollmentTracker(
                        _configuration.SessionId,
                        _configuration.TenantId,
                        EmitEvent,
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
                }

                // Desktop arrival detector — detects real user desktop for no-ESP completion
                _desktopArrivalDetector = new DesktopArrivalDetector(_logger);
                _desktopArrivalDetector.DesktopArrived += OnDesktopArrived;
                _desktopArrivalDetector.OnTraceEvent = (decision, reason, context) =>
                    EmitTraceEvent("DesktopArrivalDetector", decision, reason, context);
                _desktopArrivalDetector.Start();
                _logger.Info("DesktopArrivalDetector started — monitoring for real user desktop");

                // Agent max lifetime safety net — prevents zombie agents
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

        /// <summary>
        /// Stops all optional collectors
        /// </summary>
        private void StopOptionalCollectors()
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

            _performanceCollector?.Stop();
            _performanceCollector?.Dispose();
            _performanceCollector = null;

            _agentSelfMetricsCollector?.Stop();
            _agentSelfMetricsCollector?.Dispose();
            _agentSelfMetricsCollector = null;

            _enrollmentTracker?.Stop();
            _enrollmentTracker?.Dispose();
            _enrollmentTracker = null;
        }

        /// <summary>
        /// Fires when the agent max lifetime expires. Emits enrollment_failed with failureType "agent_timeout".
        /// The normal shutdown path (EmitEvent → enrollment_failed) handles everything from here.
        /// </summary>
        private void MaxLifetimeTimerCallback(object state)
        {
            if (_enrollmentTerminalEventSeen)
            {
                _logger.Info("Max lifetime timer fired but terminal event already seen — ignoring");
                return;
            }

            var uptimeMinutes = (DateTime.UtcNow - _agentStartTimeUtc).TotalMinutes;
            _logger.Warning($"Agent max lifetime expired after {uptimeMinutes:F0} minutes — emitting enrollment_failed");

            EmitEvent(new EnrollmentEvent
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
                }
            });
        }

        /// <summary>
        /// Fires when DesktopArrivalDetector detects explorer.exe under a real user.
        /// Emits desktop_arrived event for the timeline and notifies EnrollmentTracker.
        /// </summary>
        private void OnDesktopArrived(object sender, EventArgs e)
        {
            if (_enrollmentTerminalEventSeen)
            {
                _logger.Debug("Desktop arrived but terminal event already seen — ignoring");
                return;
            }

            // Emit informational event for the timeline (shows when user desktop appeared)
            EmitEvent(new EnrollmentEvent
            {
                SessionId = _configuration.SessionId,
                TenantId = _configuration.TenantId,
                EventType = "desktop_arrived",
                Severity = EventSeverity.Info,
                Source = "DesktopArrivalDetector",
                Phase = EnrollmentPhase.Unknown,
                Message = "User desktop detected (explorer.exe under real user)"
            });

            // Notify EnrollmentTracker — it decides whether this triggers completion
            _enrollmentTracker?.NotifyDesktopArrived();
        }

        /// <summary>
        /// Returns true for events generated by periodic timers (not real enrollment activity)
        /// </summary>
        private static bool IsPeriodicEvent(string eventType)
        {
            return eventType == "performance_snapshot" ||
                   eventType == "agent_metrics_snapshot" ||
                   eventType == "performance_collector_stopped" ||
                   eventType == "agent_metrics_collector_stopped";
        }

        /// <summary>
        /// Periodic check (every 60s) whether enrollment has gone idle.
        /// When no real event has arrived within the configured timeout window,
        /// periodic collectors are stopped to prevent session bloat.
        /// </summary>
        private void IdleCheckCallback(object state)
        {
            if (_collectorsIdleStopped)
                return;

            var idleMinutes = (DateTime.UtcNow - _lastRealEventTime).TotalMinutes;
            if (idleMinutes < _collectorIdleTimeoutMinutes)
                return;

            _logger.Info($"Collector idle timeout reached ({_collectorIdleTimeoutMinutes} min, idle for {idleMinutes:F0} min) — stopping periodic collectors");

            // Stop PerformanceCollector
            if (_performanceCollector != null)
            {
                _performanceCollector.Stop();
                _performanceCollector.Dispose();
                _performanceCollector = null;

                EmitEvent(new EnrollmentEvent
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

            // Stop AgentSelfMetricsCollector
            if (_agentSelfMetricsCollector != null)
            {
                _agentSelfMetricsCollector.Stop();
                _agentSelfMetricsCollector.Dispose();
                _agentSelfMetricsCollector = null;

                EmitEvent(new EnrollmentEvent
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

            // Stop the idle check timer — it will be restarted if collectors resume
            _idleCheckTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>
        /// Restarts periodic collectors after they were stopped due to idle timeout.
        /// Called when new real enrollment activity is detected.
        /// </summary>
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
                    EmitEvent,
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
                        EmitEvent,
                        _apiClient.NetworkMetrics,
                        _logger,
                        _agentVersion,
                        selfMetricsInterval
                    );
                    _agentSelfMetricsCollector.Start();
                }

                // Reset idle tracking and restart the check timer
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

        /// <summary>
        /// Starts the gather rule executor with rules from remote config
        /// </summary>
        private void StartGatherRuleExecutor()
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
                    EmitEvent,
                    _logger,
                    _configuration.ImeLogPathOverride
                );
                _gatherRuleExecutor.UpdateRules(config.GatherRules);
                _logger.Info($"GatherRuleExecutor started with {config.GatherRules.Count} rule(s)");
            }
            catch (Exception ex)
            {
                _logger.Error("Error starting gather rule executor", ex);
            }
        }

        /// <summary>
        /// Initializes agent analyzers from remote config.
        /// Called once during Start(), after all collectors are started.
        /// </summary>
        private void InitializeAnalyzers()
        {
            _analyzers.Clear();

            var analyzerConfig = _remoteConfigService?.CurrentConfig?.Analyzers ?? new AnalyzerConfiguration();

            if (analyzerConfig.EnableLocalAdminAnalyzer)
            {
                _analyzers.Add(new LocalAdminAnalyzer(
                    _configuration.SessionId,
                    _configuration.TenantId,
                    EmitEvent,
                    _logger,
                    analyzerConfig.LocalAdminAllowedAccounts
                ));
                _logger.Info("LocalAdminAnalyzer registered");
            }
            else
            {
                _logger.Info("LocalAdminAnalyzer disabled by remote config");
            }

            _logger.Info($"Analyzers initialized: {_analyzers.Count} active");
        }

        /// <summary>
        /// Runs all registered analyzers at agent startup — asynchronously on the ThreadPool
        /// so they do not block or delay the agent start sequence.
        /// Emitted events will be picked up by the spool watcher and uploaded normally.
        /// </summary>
        private void RunStartupAnalyzers()
        {
            if (_analyzers.Count == 0)
                return;

            // Capture the list snapshot — _analyzers is not modified after Start()
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

        /// <summary>
        /// Runs all registered analyzers at shutdown / enrollment completion.
        /// Emits end-state findings for delta detection against the startup results.
        /// </summary>
        private void RunShutdownAnalyzers()
        {
            if (_analyzers.Count == 0)
                return;

            _logger.Info($"Running {_analyzers.Count} shutdown analyzer(s)");

            foreach (var analyzer in _analyzers)
            {
                try
                {
                    analyzer.AnalyzeAtShutdown();
                }
                catch (Exception ex)
                {
                    _logger.Error($"Analyzer {analyzer.Name} threw during shutdown", ex);
                }
            }
        }

        /// <summary>
        /// Registers the session with the backend. Retries with exponential backoff
        /// to handle network not being ready during OOBE boot (WhiteGlove Part 2).
        /// </summary>
        private async Task RegisterSessionAsync()
        {
            const int maxAttempts = 5;

            var registration = new SessionRegistration
            {
                SessionId = _configuration.SessionId,
                TenantId = _configuration.TenantId,
                SerialNumber = DeviceInfoProvider.GetSerialNumber(),
                Manufacturer = DeviceInfoProvider.GetManufacturer(),
                Model = DeviceInfoProvider.GetModel(),
                DeviceName = Environment.MachineName,
                OsName = DeviceInfoProvider.GetOsName(),
                OsBuild = DeviceInfoProvider.GetOsBuild(),
                OsDisplayVersion = DeviceInfoProvider.GetOsDisplayVersion(),
                OsEdition = DeviceInfoProvider.GetOsEdition(),
                OsLanguage = System.Globalization.CultureInfo.CurrentCulture.Name,
                StartedAt = DateTime.UtcNow,
                AgentVersion = _agentVersion,
                EnrollmentType = EnrollmentTracker.DetectEnrollmentTypeStatic(),
                IsHybridJoin = EnrollmentTracker.DetectHybridJoinStatic(),
                IsUserDriven = true
            };

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    _logger.Info($"Registering session with backend (attempt {attempt}/{maxAttempts})");

                    var response = await _apiClient.RegisterSessionAsync(registration);

                    if (response.Success)
                    {
                        _logger.Info($"Session registered successfully: {response.SessionId}");
                        return;
                    }

                    _logger.Warning($"Session registration failed: {response.Message}");
                }
                catch (BackendAuthException ex)
                {
                    _logger.Error($"Session registration authentication failed: {ex.Message}");
                    HandleAuthFailure();
                    return; // Auth failure is not retryable
                }
                catch (Exception ex)
                {
                    _logger.Error($"Failed to register session (attempt {attempt}/{maxAttempts})", ex);

                    if (attempt == maxAttempts)
                    {
                        // Report to emergency channel on final failure
                        _ = _emergencyReporter.TrySendAsync(
                            AgentErrorType.RegisterSessionFailed,
                            ex.Message);
                        return;
                    }
                }

                // Exponential backoff: 2s, 4s, 8s, 16s
                var delaySeconds = (int)Math.Pow(2, attempt);
                _logger.Info($"Retrying session registration in {delaySeconds}s");
                await Task.Delay(delaySeconds * 1000);
            }
        }

        /// <summary>
        /// Stops the monitoring service
        /// </summary>
        public void Stop()
        {
            _logger.Info("Stopping monitoring service");

            // Stop FileSystemWatcher
            _spool.StopWatching();

            // Stop event collectors
            StopEventCollectors();

            // Run shutdown analyzers before final event (captures end-state for delta detection)
            RunShutdownAnalyzers();

            // Emit final event
            EmitEvent(new EnrollmentEvent
            {
                SessionId = _configuration.SessionId,
                TenantId = _configuration.TenantId,
                EventType = "agent_stopped",
                Severity = EventSeverity.Info,
                Source = "Agent",
                Phase = _lastPhase ?? EnrollmentPhase.Unknown,
                Message = "Autopilot Monitor Agent stopped"
            });

            // Final upload attempt
            UploadEventsAsync().Wait(TimeSpan.FromSeconds(10));

            _logger.Info("Monitoring service stopped");

            // Unblock WaitForCompletion() in background/SYSTEM mode
            _completionEvent.Set();
        }

        /// <summary>
        /// Blocks the calling thread until the service stops (used when running as Scheduled Task
        /// under SYSTEM where there is no interactive Console.ReadLine() to keep the process alive).
        /// </summary>
        public void WaitForCompletion()
        {
            _completionEvent.Wait();
        }

        /// <summary>
        /// Triggers cleanup without running enrollment monitoring.
        /// Used when enrollment complete marker is detected on startup (cleanup retry).
        /// </summary>
        public void TriggerCleanup()
        {
            _logger.Info("TriggerCleanup invoked - executing cleanup without enrollment monitoring");

            if (_configuration.SelfDestructOnComplete)
            {
                _cleanupService.ExecuteSelfDestruct();
            }
            else
            {
                _logger.Info("SelfDestructOnComplete is disabled - nothing to clean up");
            }
        }

        /// <summary>
        /// Stops all event collection components
        /// </summary>
        private void StopEventCollectors()
        {
            _logger.Info("Stopping event collectors");

            try
            {
                _espAndHelloTracker?.Stop();
                _espAndHelloTracker?.Dispose();

                _logReplay?.Stop();
                _logReplay?.Dispose();

                // Stop optional collectors
                StopOptionalCollectors();

                // Stop gather rule executor
                _gatherRuleExecutor?.Dispose();
                _gatherRuleExecutor = null;

                _logger.Info("Event collectors stopped");
            }
            catch (Exception ex)
            {
                _logger.Error("Error stopping event collectors", ex);
            }
        }

        /// <summary>
        /// Emits an event (adds to spool)
        /// </summary>
    }
}
