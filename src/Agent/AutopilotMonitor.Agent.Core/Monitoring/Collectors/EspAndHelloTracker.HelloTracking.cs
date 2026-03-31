using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.Core.Monitoring.Collectors
{
    /// <summary>
    /// Partial: Hello policy detection, User Device Registration event handling, and Hello timers.
    /// </summary>
    public partial class EspAndHelloTracker
    {
        private void CheckHelloPolicy()
        {
            try
            {
                var (isEnabled, source) = DetectHelloPolicy();

                lock (_stateLock)
                {
                    // If policy was not configured before but is now, update state and emit event
                    if (!_isPolicyConfigured && isEnabled.HasValue)
                    {
                        _isPolicyConfigured = true;
                        _isHelloPolicyEnabled = isEnabled.Value;
                        var status = isEnabled.Value ? "enabled" : "disabled";

                        _onEventCollected(new EnrollmentEvent
                        {
                            SessionId = _sessionId,
                            TenantId = _tenantId,
                            EventType = "hello_policy_detected",
                            Severity = EventSeverity.Info,
                            Source = "EspAndHelloTracker",
                            Phase = EnrollmentPhase.Unknown,
                            Message = $"Windows Hello for Business policy detected: {status} (via {source})",
                            Data = new Dictionary<string, object>
                            {
                                { "helloEnabled", isEnabled.Value },
                                { "policySource", source }
                            }
                        });

                        _logger.Info($"WHfB policy detected: {status} (source: {source})");

                        // Stop periodic policy check - we found what we were looking for
                        if (_policyCheckTimer != null)
                        {
                            _policyCheckTimer.Dispose();
                            _policyCheckTimer = null;
                            _logger.Debug("Stopped periodic Hello policy check - policy has been detected");
                        }
                    }
                    else if (!_isPolicyConfigured && !isEnabled.HasValue)
                    {
                        // Policy still not found - this is normal during early enrollment (Debug level to keep UI clean)
                        _logger.Debug("Periodic Hello policy check: No WHfB policy found yet - will check again");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Error checking WHfB policy: {ex.Message}");
            }
        }

        private (bool? isEnabled, string source) DetectHelloPolicy()
        {
            // 1. Check CSP/Intune-delivered policy (per-tenant paths)
            // Policy can be device-scoped: {tenantId}\Device\Policies
            // or user-scoped: {tenantId}\{userSid}\Policies
            try
            {
                using (var baseCspKey = Registry.LocalMachine.OpenSubKey(CspPolicyBasePath, false))
                {
                    if (baseCspKey != null)
                    {
                        foreach (var tenantSubKey in baseCspKey.GetSubKeyNames())
                        {
                            using (var tenantKey = baseCspKey.OpenSubKey(tenantSubKey, false))
                            {
                                if (tenantKey != null)
                                {
                                    // Check all subkeys (Device or user SIDs like S-1-5-...)
                                    foreach (var scopeSubKey in tenantKey.GetSubKeyNames())
                                    {
                                        using (var policiesKey = tenantKey.OpenSubKey($@"{scopeSubKey}\Policies", false))
                                        {
                                            if (policiesKey != null)
                                            {
                                                var value = policiesKey.GetValue("UsePassportForWork");
                                                if (value != null)
                                                {
                                                    var scope = scopeSubKey.Equals("Device", StringComparison.OrdinalIgnoreCase)
                                                        ? "device"
                                                        : "user";
                                                    return (Convert.ToInt32(value) == 1, $"CSP/Intune ({scope}-scoped)");
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            // 2. Check GPO-delivered policy
            try
            {
                using (var gpoKey = Registry.LocalMachine.OpenSubKey(GpoPolicyPath, false))
                {
                    if (gpoKey != null)
                    {
                        var value = gpoKey.GetValue("Enabled");
                        if (value != null)
                        {
                            return (Convert.ToInt32(value) == 1, "GPO");
                        }
                    }
                }
            }
            catch { }

            return (null, null);
        }

        private void StartEventLogWatcher()
        {
            try
            {
                // Watch for specific WHfB-related event IDs
                var query = new EventLogQuery(
                    EventLogChannel,
                    PathType.LogName,
                    "*[System[(EventID=300 or EventID=301 or EventID=358 or EventID=360 or EventID=362 or EventID=376)]]"
                );

                _watcher = new System.Diagnostics.Eventing.Reader.EventLogWatcher(query);
                _watcher.EventRecordWritten += OnEventRecordWritten;
                _watcher.Enabled = true;

                _logger.Info($"Started watching: {EventLogChannel}");
            }
            catch (EventLogNotFoundException)
            {
                _logger.Warning($"Event log not found: {EventLogChannel} (normal if not on a real device)");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to start Hello event log watcher", ex);
            }
        }

        private void OnEventRecordWritten(object sender, EventRecordWrittenEventArgs e)
        {
            if (e.EventRecord == null)
                return;

            try
            {
                var record = e.EventRecord;
                var eventId = record.Id;
                if (!TrackedEventIds.Contains(eventId))
                    return;

                ProcessHelloEvent(
                    eventId,
                    (record.TimeCreated ?? DateTime.UtcNow).ToUniversalTime(),
                    record.ProviderName ?? "",
                    isBackfill: false);
            }
            catch (Exception ex)
            {
                _logger.Error("Error processing Hello event record", ex);
            }
        }

        private void ProcessHelloEvent(int eventId, DateTime timestamp, string providerName, bool isBackfill)
        {
            string eventType;
            EventSeverity severity;
            string message;
            bool shouldTriggerHelloCompleted = false;

            switch (eventId)
            {
                case EventId_ProvisioningWillLaunch: // 358
                    eventType = "hello_provisioning_willlaunch";
                    severity = EventSeverity.Info;
                    message = "Windows Hello for Business provisioning will launch - prerequisites passed (snapshot only, not a final state)";
                    _logger.Info("Windows Hello provisioning will launch - prerequisites passed (snapshot only, not a final state)");
                    break;

                case EventId_NgcKeyRegistered: // 300
                    eventType = "hello_provisioning_completed";
                    severity = EventSeverity.Info;
                    message = "Windows Hello for Business provisioned successfully - NGC key registered";
                    shouldTriggerHelloCompleted = MarkHelloCompleted();
                    _logger.Info("Windows Hello provisioning COMPLETED successfully");
                    break;

                case EventId_NgcKeyRegistrationFailed: // 301
                    eventType = "hello_provisioning_failed";
                    severity = EventSeverity.Error;
                    message = "Windows Hello for Business provisioning failed - NGC key registration error";
                    shouldTriggerHelloCompleted = MarkHelloCompleted();
                    _logger.Info("Windows Hello provisioning COMPLETED with failure");
                    break;

                case EventId_ProvisioningWillNotLaunch: // 360
                    eventType = "hello_provisioning_willnotlaunch";
                    severity = EventSeverity.Warning;
                    message = "Windows Hello for Business provisioning prerequisites not met (snapshot only, not a final state)";
                    // DO NOT mark Hello as completed - event 360 is just a snapshot and can change
                    _logger.Info("Windows Hello provisioning prerequisites not met (snapshot, not terminal)");
                    break;

                case EventId_ProvisioningBlocked: // 362
                    eventType = "hello_provisioning_blocked";
                    severity = EventSeverity.Warning;
                    message = "Windows Hello for Business provisioning blocked";
                    shouldTriggerHelloCompleted = MarkHelloCompleted();
                    _logger.Info("Windows Hello provisioning COMPLETED (blocked)");
                    break;

                case EventId_PinStatus: // 376
                    eventType = "hello_pin_status";
                    severity = EventSeverity.Info;
                    message = "Windows Hello PIN status update";
                    break;

                default:
                    return;
            }

            // We only need the event ID and provider for tracking purposes.
            // The full description can contain sensitive data (e.g. server responses with key IDs, UPNs, tokens).
            var data = new Dictionary<string, object>
            {
                { "windowsEventId", eventId },
                { "providerName", providerName ?? "" },
                { "description", "truncated" },
                { "eventTime", timestamp.ToString("o") }
            };

            if (isBackfill)
            {
                data["backfill"] = true;
            }

            _onEventCollected(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                Timestamp = timestamp,
                EventType = eventType,
                Severity = severity,
                Source = "EspAndHelloTracker",
                Phase = EnrollmentPhase.Unknown,
                Message = message,
                Data = data,
                ImmediateUpload = shouldTriggerHelloCompleted || eventType == "hello_provisioning_failed"
            });

            _logger.Info($"Hello event detected: {eventType} (EventID {eventId}{(isBackfill ? ", backfill" : "")})");

            if (shouldTriggerHelloCompleted)
            {
                try { HelloCompleted?.Invoke(this, EventArgs.Empty); } catch { }
            }
        }

        private bool MarkHelloCompleted()
        {
            lock (_stateLock)
            {
                if (_isHelloCompleted)
                {
                    _logger.Debug("Hello terminal event received but Hello is already marked completed");
                    return false;
                }

                _isHelloCompleted = true;
                HelloOutcome = "completed";
                StopHelloCompletionTimerLocked();
                return true;
            }
        }

        /// <summary>
        /// Called when the Hello wait timer expires after ESP exit.
        /// If Hello wizard hasn't started by now, we assume it's not configured or was skipped.
        /// Mark Hello as completed so enrollment can proceed.
        /// </summary>
        private void OnHelloWaitTimeout(object state)
        {
            lock (_stateLock)
            {
                if (_helloWizardStarted)
                {
                    // Hello wizard already started, this timeout is obsolete
                    _logger.Debug("Hello wait timeout fired but wizard already started - ignoring");
                    return;
                }

                if (_isHelloCompleted)
                {
                    // Hello already completed via events 300/301 - timeout is obsolete
                    _logger.Debug("Hello wait timeout fired but Hello already completed - ignoring");
                    return;
                }

                // Timeout expired without Hello wizard starting.
                // If Hello policy is known to be ENABLED, the wizard simply hasn't appeared yet
                // (e.g. Windows is still setting up prerequisites). Do NOT declare enrollment
                // complete — start the long HelloCompletion timer and keep waiting.
                // If Hello policy is disabled/unknown, assume Hello is not configured and proceed.
                if (_isHelloPolicyEnabled)
                {
                    _logger.Info($"Hello wait timeout ({_helloWaitTimeoutSeconds}s) expired but Hello policy is enabled — " +
                                 $"wizard not yet visible, starting long completion timer ({HelloCompletionTimeoutSeconds}s)");

                    _onEventCollected(new EnrollmentEvent
                    {
                        SessionId = _sessionId,
                        TenantId = _tenantId,
                        EventType = "hello_wait_timeout",
                        Severity = EventSeverity.Info,
                        Source = "EspAndHelloTracker",
                        Phase = EnrollmentPhase.Unknown,
                        Message = $"Hello wizard did not start within {_helloWaitTimeoutSeconds}s after ESP exit — " +
                                  $"Hello policy is enabled, waiting up to {HelloCompletionTimeoutSeconds}s for wizard",
                        Data = new Dictionary<string, object>
                        {
                            { "timeoutSeconds", _helloWaitTimeoutSeconds },
                            { "espExitDetected", _espExitDetected },
                            { "helloPolicyEnabled", true },
                            { "action", "extended_wait" }
                        },
                        ImmediateUpload = true
                    });

                    // Arm the long completion timer so we don't wait forever
                    StartHelloCompletionTimerLocked();
                    return;
                }

                // Hello policy disabled or unknown — assume Hello is not configured
                _logger.Info($"Hello wait timeout ({_helloWaitTimeoutSeconds}s) expired without Hello wizard starting");
                _logger.Info("Hello policy not detected as enabled — assuming Hello is not configured or was skipped");

                _isHelloCompleted = true;
                HelloOutcome = "not_configured";
                StopHelloCompletionTimerLocked();

                // Emit event for tracking
                _onEventCollected(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId = _tenantId,
                    EventType = "hello_wait_timeout",
                    Severity = EventSeverity.Info,
                    Source = "EspAndHelloTracker",
                    Phase = EnrollmentPhase.Unknown,
                    Message = $"Hello wizard did not start within {_helloWaitTimeoutSeconds}s after ESP exit - assuming not configured",
                    Data = new Dictionary<string, object>
                    {
                        { "timeoutSeconds", _helloWaitTimeoutSeconds },
                        { "espExitDetected", _espExitDetected },
                        { "helloPolicyEnabled", false },
                        { "action", "enrollment_complete" }
                    },
                    ImmediateUpload = true
                });

                // Trigger HelloCompleted so enrollment can proceed
                try
                {
                    HelloCompleted?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    _logger.Error("Error invoking HelloCompleted from timeout", ex);
                }
            }
        }

        /// <summary>
        /// Resets Hello tracking state when ESP resumes after a mid-enrollment reboot (hybrid join).
        /// Stops all running timers and clears Hello state so the timer chain restarts fresh when
        /// ESP exits again for real.
        /// </summary>
        public void ResetForEspResumption()
        {
            lock (_stateLock)
            {
                _helloWaitTimer?.Dispose();
                _helloWaitTimer = null;
                _helloCompletionTimer?.Dispose();
                _helloCompletionTimer = null;

                _isHelloCompleted = false;
                _helloWizardStarted = false;
                _espExitDetected = false;
                HelloOutcome = null;
            }
            _logger.Info("EspAndHelloTracker: Reset for ESP resumption — Hello tracking restarted");
        }

        /// <summary>
        /// Starts the Hello wait timer. Should be called by EnrollmentTracker when AccountSetup phase exits.
        /// The timer waits for Hello wizard to start (event 62404) within the configured timeout.
        /// If timeout expires without Hello wizard, marks Hello as completed so enrollment can proceed.
        /// </summary>
        public void StartHelloWaitTimer()
        {
            lock (_stateLock)
            {
                // Don't start if Hello already started or completed
                if (_helloWizardStarted)
                {
                    _logger.Debug("StartHelloWaitTimer called but Hello wizard already started - skipping");
                    return;
                }

                if (_isHelloCompleted)
                {
                    _logger.Debug("StartHelloWaitTimer called but Hello already completed - skipping");
                    return;
                }

                // Don't start if timer already running
                if (_helloWaitTimer != null)
                {
                    _logger.Debug("StartHelloWaitTimer called but timer already running - skipping");
                    return;
                }

                _logger.Info($"Starting Hello wait timer ({_helloWaitTimeoutSeconds}s) - waiting for Hello wizard to start");
                _helloWaitTimer = new System.Threading.Timer(
                    OnHelloWaitTimeout,
                    null,
                    TimeSpan.FromSeconds(_helloWaitTimeoutSeconds),
                    TimeSpan.FromMilliseconds(-1) // One-shot timer
                );
            }
        }

        private void StartHelloCompletionTimerLocked()
        {
            if (_isHelloCompleted)
                return;

            if (_helloCompletionTimer != null)
                return;

            _logger.Info($"Starting Hello completion timer ({HelloCompletionTimeoutSeconds}s) - waiting for terminal Hello event (300/301/362)");
            _helloCompletionTimer = new System.Threading.Timer(
                OnHelloCompletionTimeout,
                null,
                TimeSpan.FromSeconds(HelloCompletionTimeoutSeconds),
                TimeSpan.FromMilliseconds(-1));
        }

        private void StopHelloCompletionTimerLocked()
        {
            if (_helloCompletionTimer == null)
                return;

            try
            {
                _helloCompletionTimer.Dispose();
            }
            catch { }
            finally
            {
                _helloCompletionTimer = null;
            }
        }

        private void OnHelloCompletionTimeout(object state)
        {
            lock (_stateLock)
            {
                if (_isHelloCompleted)
                {
                    _logger.Debug("Hello completion timeout fired but Hello already completed - ignoring");
                    return;
                }

                _logger.Warning($"Hello completion timeout ({HelloCompletionTimeoutSeconds}s) expired after wizard start without terminal event");
                _isHelloCompleted = true;
                HelloOutcome = _helloWizardStarted ? "timeout" : "wizard_not_started";
                StopHelloCompletionTimerLocked();

                _onEventCollected(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId = _tenantId,
                    EventType = "hello_completion_timeout",
                    Severity = EventSeverity.Warning,
                    Source = "EspAndHelloTracker",
                    Phase = EnrollmentPhase.Unknown,
                    Message = $"Hello wizard started but no terminal event (300/301/362) arrived within {HelloCompletionTimeoutSeconds}s",
                    Data = new Dictionary<string, object>
                    {
                        { "timeoutSeconds", HelloCompletionTimeoutSeconds },
                        { "helloWizardStarted", _helloWizardStarted }
                    },
                    ImmediateUpload = true
                });

                try
                {
                    HelloCompleted?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    _logger.Error("Error invoking HelloCompleted from completion timeout", ex);
                }
            }
        }

        private void BackfillRecentTerminalHelloEvents()
        {
            try
            {
                var lookbackMs = BackfillLookbackMinutes * 60 * 1000;
                var query = new EventLogQuery(
                    EventLogChannel,
                    PathType.LogName,
                    $"*[System[(EventID=300 or EventID=301 or EventID=362) and TimeCreated[timediff(@SystemTime) <= {lookbackMs}]]]");

                using (var reader = new EventLogReader(query))
                {
                    DateTime latestTimestamp = DateTime.MinValue;
                    int? latestEventId = null;
                    string latestProvider = string.Empty;

                    for (EventRecord record = reader.ReadEvent(); record != null; record = reader.ReadEvent())
                    {
                        using (record)
                        {
                            var eventId = record.Id;
                            if (eventId != EventId_NgcKeyRegistered && eventId != EventId_NgcKeyRegistrationFailed && eventId != EventId_ProvisioningBlocked)
                                continue;

                            var timestamp = record.TimeCreated ?? DateTime.MinValue;
                            if (timestamp < latestTimestamp)
                                continue;

                            latestTimestamp = timestamp;
                            latestEventId = eventId;
                            latestProvider = record.ProviderName ?? "";
                        }
                    }

                    if (latestEventId.HasValue)
                    {
                        _logger.Info($"Backfill found recent Hello terminal event: EventID {latestEventId.Value} at {latestTimestamp:O}");
                        ProcessHelloEvent(latestEventId.Value, (latestTimestamp == DateTime.MinValue ? DateTime.UtcNow : latestTimestamp).ToUniversalTime(), latestProvider, isBackfill: true);
                    }
                    else
                    {
                        _logger.Debug($"Backfill found no terminal Hello events in last {BackfillLookbackMinutes} minutes");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Hello terminal event backfill failed: {ex.Message}");
            }
        }
    }
}
