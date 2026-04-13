using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.Core.Configuration;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Agent.Core.Monitoring.Network;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.Core.Monitoring.Core
{
    /// <summary>
    /// Manages event upload lifecycle: debouncing, batched upload, auth-failure circuit breaker,
    /// and admin signal detection (kill, block, override) from upload responses.
    /// </summary>
    public class EventUploadOrchestrator : IDisposable
    {
        private readonly AgentConfiguration _configuration;
        private readonly AgentLogger _logger;
        private readonly EventSpool _spool;
        private readonly BackendApiClient _apiClient;
        private readonly EmergencyReporter _emergencyReporter;
        private readonly DistressReporter _distressReporter;
        private readonly CleanupService _cleanupService;
        private readonly Action<EnrollmentEvent> _emitEvent;
        private readonly Action<string, string, Dictionary<string, object>> _emitShutdownEvent;
        private readonly Func<bool> _isTerminalEventSeen;

        private readonly Timer _uploadTimer;
        private readonly Timer _debounceTimer;
        private readonly object _timerLock = new object();
        private readonly SemaphoreSlim _uploadSemaphore = new SemaphoreSlim(1, 1);

        private int _consecutiveAuthFailures;
        private DateTime? _firstAuthFailureTime;
        private int _consecutiveUploadFailures;

        public EventUploadOrchestrator(
            AgentConfiguration configuration,
            AgentLogger logger,
            EventSpool spool,
            BackendApiClient apiClient,
            EmergencyReporter emergencyReporter,
            DistressReporter distressReporter,
            CleanupService cleanupService,
            Action<EnrollmentEvent> emitEvent,
            Action<string, string, Dictionary<string, object>> emitShutdownEvent,
            Func<bool> isTerminalEventSeen)
        {
            _configuration = configuration;
            _logger = logger;
            _spool = spool;
            _apiClient = apiClient;
            _emergencyReporter = emergencyReporter;
            _distressReporter = distressReporter;
            _cleanupService = cleanupService;
            _emitEvent = emitEvent;
            _emitShutdownEvent = emitShutdownEvent;
            _isTerminalEventSeen = isTerminalEventSeen;

            _debounceTimer = new Timer(
                DebounceTimerCallback,
                null,
                Timeout.Infinite,
                Timeout.Infinite
            );

            _uploadTimer = new Timer(
                UploadTimerCallback,
                null,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan
            );
        }

        /// <summary>
        /// Starts the upload and fallback timers. Call after spool watcher is started.
        /// </summary>
        public void Start()
        {
            _uploadTimer.Change(
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(5)
            );
            _logger.Info("EventUploadOrchestrator started (debounce + fallback timers)");
        }

        /// <summary>
        /// Stops all upload timers. Called during terminal event processing to prevent
        /// further automatic uploads while the completion handler drives uploads manually.
        /// </summary>
        public void StopTimers()
        {
            _uploadTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>
        /// Called when new events are available in the spool (FileSystemWatcher detected new files).
        /// Uses debouncing to batch events before uploading.
        /// </summary>
        public void OnEventsAvailable(object sender, EventArgs e)
        {
            _logger.Debug("FileSystemWatcher detected new events, starting/resetting debounce timer");

            lock (_timerLock)
            {
                _debounceTimer.Change(
                    TimeSpan.FromSeconds(_configuration.UploadIntervalSeconds),
                    Timeout.InfiniteTimeSpan
                );
            }
        }

        private void DebounceTimerCallback(object state)
        {
            _logger.Debug("Debounce timer expired, uploading batched events");
            Task.Run(() => UploadEventsAsync());
        }

        private void UploadTimerCallback(object state)
        {
            Task.Run(() => UploadEventsAsync());
        }

        public async Task UploadEventsAsync()
        {
            if (!_uploadSemaphore.Wait(0))
            {
                _logger.Verbose("UploadEventsAsync: skipped — concurrent upload already in flight");
                return;
            }

            try
            {
                var events = _spool.GetBatch(_configuration.MaxBatchSize);

                if (events.Count == 0)
                {
                    _logger.Debug("No events to upload");
                    return;
                }

                var request = new IngestEventsRequest
                {
                    SessionId = _configuration.SessionId,
                    TenantId = _configuration.TenantId,
                    Events = events
                };

                var response = await _apiClient.IngestEventsAsync(request);

                if (response.DeviceKillSignal)
                {
                    _logger.Warning($"=== REMOTE KILL SIGNAL received from administrator. Spool: {_spool.GetCount()} events pending. Initiating self-destruct... ===");

                    StopTimers();

                    _emitShutdownEvent(
                        "remote_kill_signal",
                        "Agent terminated by remote kill signal from administrator",
                        new Dictionary<string, object>
                        {
                            { "pendingEvents", _spool.GetCount() },
                            { "selfDestruct", true }
                        });
                    try
                    {
                        var shutdownEvents = _spool.GetBatch(_configuration.MaxBatchSize);
                        if (shutdownEvents.Count > 0)
                        {
                            await _apiClient.IngestEventsAsync(new IngestEventsRequest
                            {
                                SessionId = _configuration.SessionId,
                                TenantId = _configuration.TenantId,
                                Events = shutdownEvents
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning($"Failed to upload shutdown event before kill: {ex.Message}");
                    }

                    _configuration.SelfDestructOnComplete = true;
                    _cleanupService.ExecuteSelfDestruct();
                    ExitProcess(0);
                    return;
                }

                if (response.DeviceBlocked)
                {
                    var unblockMsg = response.UnblockAt.HasValue
                        ? $" until {response.UnblockAt.Value:yyyy-MM-dd HH:mm:ss} UTC"
                        : string.Empty;
                    _logger.Warning($"=== DEVICE BLOCKED by administrator{unblockMsg}. Stopping all uploads for this session. ===");

                    StopTimers();
                    return;
                }

                if (!string.IsNullOrEmpty(response.AdminAction) && !_isTerminalEventSeen())
                {
                    var succeeded = string.Equals(response.AdminAction, "Succeeded", StringComparison.OrdinalIgnoreCase);
                    _logger.Warning($"=== ADMIN OVERRIDE: Session marked as {response.AdminAction} by administrator. Initiating cleanup... ===");

                    _emitEvent(new EnrollmentEvent
                    {
                        SessionId = _configuration.SessionId,
                        TenantId = _configuration.TenantId,
                        EventType = succeeded ? "enrollment_complete" : "enrollment_failed",
                        Severity = succeeded ? EventSeverity.Info : EventSeverity.Warning,
                        Source = "AdminOverride",
                        Phase = EnrollmentPhase.Complete,
                        Message = $"Session {response.AdminAction.ToLower()} by administrator — cleanup initiated",
                        Timestamp = DateTime.UtcNow,
                        Data = new Dictionary<string, object>
                        {
                            { "adminAction", response.AdminAction }
                        }
                    });
                    return;
                }

                if (response.Success)
                {
                    _spool.RemoveEvents(events);
                    _logger.Info($"Uploaded {response.EventsProcessed} events");

                    if (_consecutiveAuthFailures > 0 || _consecutiveUploadFailures > 0)
                        _logger.Info($"Upload success — resetting failure counters (auth={_consecutiveAuthFailures}, upload={_consecutiveUploadFailures})");
                    _consecutiveAuthFailures = 0;
                    _firstAuthFailureTime = null;
                    _consecutiveUploadFailures = 0;
                }
                else
                {
                    _logger.Warning($"Upload failed: {response.Message}");
                }
            }
            catch (BackendAuthException ex)
            {
                _logger.Error($"Upload authentication failed: {ex.Message}");
                HandleAuthFailure(ex.StatusCode);
            }
            catch (Exception ex)
            {
                _consecutiveUploadFailures++;
                _logger.Error("Error uploading events", ex);

                _logger.Info($"Upload failure #{_consecutiveUploadFailures}: {ex.Message}");

                if (_consecutiveUploadFailures == EmergencyReporter.ConsecutiveFailureThreshold)
                {
                    _ = _emergencyReporter.TrySendAsync(
                        AgentErrorType.IngestFailed,
                        ex.Message,
                        httpStatusCode: null,
                        sequenceNumber: 0);
                }
            }
            finally
            {
                _uploadSemaphore.Release();
            }
        }

        /// <summary>
        /// Handles an authentication failure by incrementing the counter and shutting down
        /// the agent if the configured threshold is reached. Called from upload path and
        /// session registration path.
        /// </summary>
        public void HandleAuthFailure(int httpStatusCode = 0)
        {
            _consecutiveAuthFailures++;

            if (_firstAuthFailureTime == null)
                _firstAuthFailureTime = DateTime.UtcNow;

            if (_consecutiveAuthFailures == 1 && _distressReporter != null)
            {
                var distressType = httpStatusCode == 401
                    ? DistressErrorType.AuthCertificateRejected
                    : DistressErrorType.DeviceNotRegistered;

                _ = _distressReporter.TrySendAsync(distressType,
                    $"Backend returned {httpStatusCode} during event upload",
                    httpStatusCode: httpStatusCode);
            }

            _logger.Warning($"Authentication failure {_consecutiveAuthFailures}" +
                (_configuration.MaxAuthFailures > 0 ? $"/{_configuration.MaxAuthFailures}" : "") +
                $" (first failure at {_firstAuthFailureTime.Value:HH:mm:ss})");

            if (_configuration.MaxAuthFailures > 0 && _consecutiveAuthFailures >= _configuration.MaxAuthFailures)
            {
                _logger.Error($"=== AGENT SHUTDOWN: {_consecutiveAuthFailures} consecutive authentication failures (401/403). " +
                    "The device is not authorized to send data to Autopilot Monitor. " +
                    "Check client certificate and Autopilot device validation in your tenant configuration. ===");

                _emitShutdownEvent(
                    "auth_failure",
                    $"Agent terminated: {_consecutiveAuthFailures} consecutive authentication failures exceeded threshold ({_configuration.MaxAuthFailures})",
                    new Dictionary<string, object>
                    {
                        { "consecutiveFailures", _consecutiveAuthFailures },
                        { "shutdownTrigger", "max_attempts" },
                        { "maxAuthFailures", _configuration.MaxAuthFailures }
                    });
                ExitProcess(1);
            }

            if (_configuration.AuthFailureTimeoutMinutes > 0 && _firstAuthFailureTime.HasValue)
            {
                var elapsed = DateTime.UtcNow - _firstAuthFailureTime.Value;
                if (elapsed.TotalMinutes >= _configuration.AuthFailureTimeoutMinutes)
                {
                    _logger.Error($"=== AGENT SHUTDOWN: Authentication failures persisted for {elapsed.TotalMinutes:F0} minutes " +
                        $"(timeout: {_configuration.AuthFailureTimeoutMinutes} min). " +
                        "The device is not authorized to send data to Autopilot Monitor. " +
                        "Check client certificate and Autopilot device validation in your tenant configuration. ===");

                    _emitShutdownEvent(
                        "auth_failure",
                        $"Agent terminated: authentication failures persisted for {elapsed.TotalMinutes:F0} minutes (timeout: {_configuration.AuthFailureTimeoutMinutes} min)",
                        new Dictionary<string, object>
                        {
                            { "consecutiveFailures", _consecutiveAuthFailures },
                            { "shutdownTrigger", "timeout" },
                            { "authFailureTimeoutMinutes", _configuration.AuthFailureTimeoutMinutes },
                            { "elapsedMinutes", Math.Round(elapsed.TotalMinutes, 1) }
                        });
                    ExitProcess(1);
                }
            }
        }

        internal virtual void ExitProcess(int code) => Environment.Exit(code);

        public void Dispose()
        {
            _uploadTimer?.Dispose();
            _debounceTimer?.Dispose();
        }
    }
}
