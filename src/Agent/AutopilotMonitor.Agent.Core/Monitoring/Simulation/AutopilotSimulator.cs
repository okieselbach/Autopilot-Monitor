using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Agent.Core.Monitoring.Tracking;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.Core.Monitoring.Simulation
{
    /// <summary>
    /// Simulates Autopilot enrollment using real IME log replay and real device data.
    ///
    /// Key principle: Real data, no mocks.
    /// - Device info: collected from actual WMI/Registry on the machine running the simulator
    /// - App tracking: replayed from real IME log files with time-compressed delays
    /// - Performance data: handled by PerformanceCollector (always on, real machine data)
    /// </summary>
    public class AutopilotSimulator : IDisposable
    {
        private readonly AgentLogger _logger;
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly Action<EnrollmentEvent> _onEventCollected;
        private readonly bool _simulateFailure;
        private readonly string _simulationLogDirectory;
        private readonly double _speedFactor;
        private readonly List<ImeLogPattern> _imeLogPatterns;

        private CancellationTokenSource _cancellationTokenSource;
        private Task _simulationTask;
        private EnrollmentTracker _enrollmentTracker;
        private bool _disposed = false;

        public AutopilotSimulator(
            string sessionId,
            string tenantId,
            Action<EnrollmentEvent> onEventCollected,
            AgentLogger logger,
            bool simulateFailure = false,
            string simulationLogDirectory = null,
            double speedFactor = 50,
            List<ImeLogPattern> imeLogPatterns = null)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _onEventCollected = onEventCollected ?? throw new ArgumentNullException(nameof(onEventCollected));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _simulateFailure = simulateFailure;
            _simulationLogDirectory = simulationLogDirectory;
            _speedFactor = speedFactor;
            _imeLogPatterns = imeLogPatterns ?? new List<ImeLogPattern>();
        }

        /// <summary>
        /// Starts the Autopilot simulation
        /// </summary>
        public void Start()
        {
            _logger.Info("Starting Autopilot Simulator (log replay mode)");

            if (string.IsNullOrEmpty(_simulationLogDirectory) || !Directory.Exists(_simulationLogDirectory))
            {
                _logger.Warning($"Simulation log directory not found or not specified: {_simulationLogDirectory}");
                _logger.Info("Simulator will only emit device info events (no log replay)");
            }

            _cancellationTokenSource = new CancellationTokenSource();
            _simulationTask = Task.Run(() => RunSimulation(_cancellationTokenSource.Token));
        }

        /// <summary>
        /// Stops the simulation
        /// </summary>
        public void Stop()
        {
            if (_cancellationTokenSource == null || _cancellationTokenSource.IsCancellationRequested)
                return;

            _logger.Info("Stopping Autopilot Simulator");

            try
            {
                _cancellationTokenSource?.Cancel();
                _simulationTask?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (ObjectDisposedException) { }
            catch (AggregateException) { }
        }

        private async Task RunSimulation(CancellationToken cancellationToken)
        {
            try
            {
                _logger.Info("Starting simulated Autopilot enrollment (real device data + log replay)");

                // Create EnrollmentTracker in simulation mode
                // This collects REAL device info from this machine and replays IME logs
                // The EnrollmentTracker will emit all real events from log replay
                _enrollmentTracker = new EnrollmentTracker(
                    _sessionId,
                    _tenantId,
                    _onEventCollected,
                    _logger,
                    _imeLogPatterns,
                    _simulationLogDirectory,
                    simulationMode: true,
                    speedFactor: _speedFactor
                );

                _enrollmentTracker.Start();
                _logger.Info($"EnrollmentTracker started in simulation mode (speed factor: {_speedFactor}x)");

                // Wait for log replay to complete or be cancelled
                // The ImeLogTracker will parse all log files and then the polling loop will idle
                // We monitor the app tracking state to know when to complete
                var checkInterval = TimeSpan.FromSeconds(5);
                var maxWaitTime = TimeSpan.FromMinutes(10); // Safety timeout
                var elapsed = TimeSpan.Zero;
                var allAppsCompleted = false;

                while (!cancellationToken.IsCancellationRequested && elapsed < maxWaitTime)
                {
                    await Task.Delay(checkInterval, cancellationToken);
                    elapsed += checkInterval;

                    // Check if all apps have been tracked and completed
                    var packageStates = _enrollmentTracker.ImeTracker?.PackageStates;
                    if (packageStates != null && packageStates.CountAll > 0 && packageStates.IsAllCompleted())
                    {
                        allAppsCompleted = true;
                        _logger.Info($"Simulator: all {packageStates.CountAll} apps completed after {elapsed.TotalSeconds:F0}s");
                        break;
                    }
                }

                if (cancellationToken.IsCancellationRequested) return;

                // Note: enrollment_complete event is now emitted by EnrollmentTracker
                // when it detects "Completed user session" in the IME log.
                // We only emit failure events here if simulation fails.
                if (_simulateFailure && !allAppsCompleted)
                {
                    EmitEvent("enrollment_failed", "Enrollment failed: app installation timeout",
                        EventSeverity.Critical, EnrollmentPhase.Failed,
                        new Dictionary<string, object>
                        {
                            { "failedPhase", "AppInstallation" },
                            { "reason", "App installation did not complete within timeout" }
                        });
                }

                _logger.Info("Simulated Autopilot enrollment replay completed (enrollment_complete will be emitted when IME log shows user session completion)");
            }
            catch (OperationCanceledException)
            {
                _logger.Info("Autopilot simulation cancelled");
            }
            catch (Exception ex)
            {
                _logger.Error("Error in Autopilot simulation", ex);
            }
        }

        private void EmitEvent(string eventType, string message, EventSeverity severity,
            EnrollmentPhase phase, Dictionary<string, object> data = null)
        {
            try
            {
                _onEventCollected(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId = _tenantId,
                    Timestamp = DateTime.UtcNow,
                    EventType = eventType,
                    Severity = severity,
                    Source = "Simulator",
                    Phase = phase,
                    Message = message,
                    Data = data ?? new Dictionary<string, object>()
                });
                _logger.Debug($"Simulator event: {eventType} - {message}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Error emitting simulator event: {eventType}", ex);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            Stop();
            _enrollmentTracker?.Dispose();
            _cancellationTokenSource?.Dispose();
            _disposed = true;
        }
    }
}
