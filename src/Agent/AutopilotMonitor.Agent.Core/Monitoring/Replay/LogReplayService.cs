using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Agent.Core.Monitoring.Tracking;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.Core.Monitoring.Replay
{
    /// <summary>
    /// Replays a real Autopilot enrollment using IME log files with time compression.
    ///
    /// Key principle: Real data, no mocks.
    /// - Device info: collected from actual WMI/Registry on the machine running the replay
    /// - App tracking: replayed from real IME log files with configurable speed factor
    /// - Performance data: handled by PerformanceCollector (always on, real machine data)
    /// </summary>
    public class LogReplayService : IDisposable
    {
        private readonly AgentLogger _logger;
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly Action<EnrollmentEvent> _onEventCollected;
        private readonly string _replayLogDir;
        private readonly double _speedFactor;
        private readonly List<ImeLogPattern> _imeLogPatterns;

        private CancellationTokenSource _cancellationTokenSource;
        private Task _replayTask;
        private EnrollmentTracker _enrollmentTracker;
        private bool _disposed = false;

        public LogReplayService(
            string sessionId,
            string tenantId,
            Action<EnrollmentEvent> onEventCollected,
            AgentLogger logger,
            string replayLogDir = null,
            double speedFactor = 50,
            List<ImeLogPattern> imeLogPatterns = null)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _onEventCollected = onEventCollected ?? throw new ArgumentNullException(nameof(onEventCollected));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _replayLogDir = replayLogDir;
            _speedFactor = speedFactor;
            _imeLogPatterns = imeLogPatterns ?? new List<ImeLogPattern>();
        }

        /// <summary>
        /// Starts log replay
        /// </summary>
        public void Start()
        {
            _logger.Info("Starting log replay service");

            if (string.IsNullOrEmpty(_replayLogDir) || !Directory.Exists(_replayLogDir))
            {
                _logger.Warning($"Replay log directory not found or not specified: {_replayLogDir}");
                _logger.Info("Log replay will only emit device info events (no log replay)");
            }

            _cancellationTokenSource = new CancellationTokenSource();
            _replayTask = Task.Run(() => RunReplay(_cancellationTokenSource.Token));
        }

        /// <summary>
        /// Stops the log replay
        /// </summary>
        public void Stop()
        {
            if (_cancellationTokenSource == null || _cancellationTokenSource.IsCancellationRequested)
                return;

            _logger.Info("Stopping log replay service");

            try
            {
                _cancellationTokenSource?.Cancel();
                _replayTask?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (ObjectDisposedException) { }
            catch (AggregateException) { }
        }

        private async Task RunReplay(CancellationToken cancellationToken)
        {
            try
            {
                _logger.Info($"Starting log replay (real device data + IME log replay, speed factor: {_speedFactor}x)");

                _enrollmentTracker = new EnrollmentTracker(
                    _sessionId,
                    _tenantId,
                    _onEventCollected,
                    _logger,
                    _imeLogPatterns,
                    _replayLogDir,
                    simulationMode: true,
                    speedFactor: _speedFactor
                );

                _enrollmentTracker.Start();
                _logger.Info($"EnrollmentTracker started in replay mode (speed factor: {_speedFactor}x)");

                // Wait for log replay to complete or be cancelled
                var checkInterval = TimeSpan.FromSeconds(5);
                var maxWaitTime = TimeSpan.FromMinutes(10); // Safety timeout
                var elapsed = TimeSpan.Zero;

                while (!cancellationToken.IsCancellationRequested && elapsed < maxWaitTime)
                {
                    await Task.Delay(checkInterval, cancellationToken);
                    elapsed += checkInterval;

                    var packageStates = _enrollmentTracker.ImeTracker?.PackageStates;
                    if (packageStates != null && packageStates.CountAll > 0 && packageStates.IsAllCompleted())
                    {
                        _logger.Info($"Log replay: all {packageStates.CountAll} apps completed after {elapsed.TotalSeconds:F0}s");
                        break;
                    }
                }

                _logger.Info("Log replay completed (enrollment_complete will be emitted when IME log shows user session completion)");
            }
            catch (OperationCanceledException)
            {
                _logger.Info("Log replay cancelled");
            }
            catch (Exception ex)
            {
                _logger.Error("Error in log replay", ex);
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
