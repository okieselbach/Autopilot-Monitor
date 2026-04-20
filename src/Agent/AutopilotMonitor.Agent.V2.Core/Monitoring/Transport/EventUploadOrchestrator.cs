using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Transport
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
        private ServerActionDispatcher _serverActionDispatcher;

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
        /// Wires the server-action dispatcher. Called by <see cref="MonitoringService"/> after the
        /// orchestrator is constructed, once all other services exist. Optional — if null, actions
        /// in ingest responses are simply ignored.
        /// </summary>
        public void SetServerActionDispatcher(ServerActionDispatcher dispatcher)
        {
            _serverActionDispatcher = dispatcher;
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

                // DeviceBlocked is intentionally NOT synthesized into a ServerAction: it's a
                // non-terminal quarantine (UnblockAt can auto-resume the session later). Treat it
                // as a straight "stop uploads for now" signal.
                if (response.DeviceBlocked)
                {
                    var unblockMsg = response.UnblockAt.HasValue
                        ? $" until {response.UnblockAt.Value:yyyy-MM-dd HH:mm:ss} UTC"
                        : string.Empty;
                    _logger.Warning($"=== DEVICE BLOCKED by administrator{unblockMsg}. Stopping all uploads for this session. ===");

                    StopTimers();
                    return;
                }

                // Synthesize legacy kill + admin-override signals into ServerActions so the whole
                // "server told us to stop" surface runs through a single shutdown handler. This
                // removes three divergent code paths (kill / admin / terminate) and guarantees
                // every terminal path respects SelfDestructOnComplete — no more zombie agents
                // after the operator clicks Mark-Failed in the portal.
                List<ServerAction> synthesized = null;

                if (response.DeviceKillSignal)
                {
                    synthesized = new List<ServerAction>
                    {
                        new ServerAction
                        {
                            Type = ServerActionTypes.TerminateSession,
                            Reason = "DeviceKillSignal from administrator",
                            QueuedAt = DateTime.UtcNow,
                            Params = new Dictionary<string, string>
                            {
                                // Kill overrides everything: force self-destruct even if local config says otherwise,
                                // and no grace period — the operator decided this device must go NOW.
                                { "forceSelfDestruct", "true" },
                                { "gracePeriodSeconds", "0" },
                                { "origin", "kill_signal" }
                            }
                        }
                    };
                }
                else if (!string.IsNullOrEmpty(response.AdminAction) && !_isTerminalEventSeen())
                {
                    // Admin marked the session terminal from the portal (Mark-Failed / Mark-Succeeded).
                    // Soft shutdown: config decides whether to self-destruct — but in Prod
                    // SelfDestructOnComplete=true, so no zombies.
                    synthesized = new List<ServerAction>
                    {
                        new ServerAction
                        {
                            Type = ServerActionTypes.TerminateSession,
                            Reason = $"Admin marked session as {response.AdminAction}",
                            QueuedAt = DateTime.UtcNow,
                            Params = new Dictionary<string, string>
                            {
                                { "adminOutcome", response.AdminAction },
                                { "gracePeriodSeconds", "0" },
                                { "origin", "admin_action" }
                            }
                        }
                    };
                }

                // Merge synthesized signals with real server actions, then dispatch once.
                // Order: synthesized first (they're the highest-priority stop signals).
                if (synthesized != null || (response.Actions != null && response.Actions.Count > 0))
                {
                    var batch = synthesized ?? new List<ServerAction>();
                    if (response.Actions != null) batch.AddRange(response.Actions);

                    if (_serverActionDispatcher != null)
                    {
                        try
                        {
                            await _serverActionDispatcher.DispatchAsync(batch);
                        }
                        catch (Exception ex)
                        {
                            _logger.Error($"ServerActionDispatcher.DispatchAsync threw (batch aborted)", ex);
                        }
                    }
                    else if (synthesized != null)
                    {
                        // Defensive: if a synthesized kill/admin signal arrives before the dispatcher
                        // was wired, fall back to a bare-minimum shutdown so we don't leave a zombie.
                        _logger.Error("Kill/Admin signal received but no dispatcher wired — forcing exit");
                        StopTimers();
                        _configuration.SelfDestructOnComplete = true;
                        _cleanupService.ExecuteSelfDestruct();
                        ExitProcess(1);
                    }
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
