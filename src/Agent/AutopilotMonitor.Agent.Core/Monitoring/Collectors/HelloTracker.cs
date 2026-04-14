using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.Core.Monitoring.Collectors
{
    /// <summary>
    /// Tracks Windows Hello for Business (WHfB) provisioning during Autopilot enrollment.
    ///
    /// Signals:
    ///   - User Device Registration/Admin: 300/301/358/360/362/376 (NGC key lifecycle)
    ///   - HelloForBusiness/Operational: 3024/6045 (processing start/stop + skip HRESULT)
    ///   - Registry (CSP + GPO): PassportForWork policy detection
    ///   - External "wizard started" signal from ESP (Shell-Core 62404)
    ///   - External "ESP exited" signal from ESP (Shell-Core 62407)
    ///
    /// Completion outcomes: "completed", "skipped", "timeout", "not_configured", "wizard_not_started".
    /// </summary>
    internal sealed class HelloTracker : IDisposable
    {
        internal const string UdrEventLogChannel = "Microsoft-Windows-User Device Registration/Admin";
        internal const string HelloForBusinessEventLogChannel = "Microsoft-Windows-HelloForBusiness/Operational";

        internal const string CspPolicyBasePath = @"SOFTWARE\Microsoft\Policies\PassportForWork";
        internal const string GpoPolicyPath = @"SOFTWARE\Policies\Microsoft\PassportForWork";

        internal const int EventId_NgcKeyRegistered = 300;
        internal const int EventId_NgcKeyRegistrationFailed = 301;
        internal const int EventId_ProvisioningWillLaunch = 358;
        internal const int EventId_ProvisioningWillNotLaunch = 360;
        internal const int EventId_ProvisioningBlocked = 362;
        internal const int EventId_PinStatus = 376;

        internal const int EventId_HelloForBusiness_ProcessingStarted = 3024;
        internal const int EventId_HelloForBusiness_ProcessingStopped = 6045;

        internal const string HResult_UserSkippedHello = "0x801C044F";

        internal const int HelloCompletionTimeoutSeconds = 300;
        internal const int BackfillLookbackMinutes = 5;

        private static readonly HashSet<int> TrackedUdrEventIds = new HashSet<int>
        {
            EventId_NgcKeyRegistered,
            EventId_NgcKeyRegistrationFailed,
            EventId_ProvisioningWillLaunch,
            EventId_ProvisioningWillNotLaunch,
            EventId_ProvisioningBlocked,
            EventId_PinStatus
        };

        private static readonly HashSet<int> TrackedHelloForBusinessEventIds = new HashSet<int>
        {
            EventId_HelloForBusiness_ProcessingStarted,
            EventId_HelloForBusiness_ProcessingStopped
        };

        private static readonly Regex HResultPattern = new Regex(@"0x[0-9A-Fa-f]{8}", RegexOptions.Compiled);

        private readonly AgentLogger _logger;
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly Action<EnrollmentEvent> _onEventCollected;
        private readonly int _helloWaitTimeoutSeconds;

        private EventLogWatcher _udrWatcher;
        private EventLogWatcher _helloForBusinessWatcher;
        private System.Threading.Timer _policyCheckTimer;
        private System.Threading.Timer _helloWaitTimer;
        private System.Threading.Timer _helloCompletionTimer;

        private bool _isPolicyConfigured;
        private bool _isHelloPolicyEnabled;
        private bool _isHelloCompleted;
        private bool _helloWizardStarted;
        private bool _espExitSeen;
        private readonly object _stateLock = new object();

        public event EventHandler HelloCompleted;

        public string HelloOutcome { get; private set; }

        public bool IsPolicyConfigured { get { lock (_stateLock) { return _isPolicyConfigured; } } }

        public bool IsHelloCompleted { get { lock (_stateLock) { return _isHelloCompleted; } } }

        public HelloTracker(
            string sessionId,
            string tenantId,
            Action<EnrollmentEvent> onEventCollected,
            AgentLogger logger,
            int helloWaitTimeoutSeconds = 30)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _onEventCollected = onEventCollected ?? throw new ArgumentNullException(nameof(onEventCollected));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _helloWaitTimeoutSeconds = helloWaitTimeoutSeconds;
        }

        // =====================================================================
        // Lifecycle
        // =====================================================================

        public void Start()
        {
            CheckHelloPolicy();

            _policyCheckTimer = new System.Threading.Timer(
                _ => CheckHelloPolicy(),
                null,
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(10));

            StartUdrEventLogWatcher();
            StartHelloForBusinessEventLogWatcher();

            BackfillRecentTerminalHelloEvents();
            BackfillRecentHelloForBusinessEvents();
        }

        public void Stop()
        {
            DisposeTimer(ref _policyCheckTimer, "policy check");
            DisposeTimer(ref _helloWaitTimer, "Hello wait");
            DisposeTimer(ref _helloCompletionTimer, "Hello completion");

            if (_udrWatcher != null)
            {
                try
                {
                    _udrWatcher.Enabled = false;
                    _udrWatcher.EventRecordWritten -= OnUdrEventRecordWritten;
                    _udrWatcher.Dispose();
                    _udrWatcher = null;
                }
                catch (Exception ex) { _logger.Error("Error stopping UDR watcher", ex); }
            }

            if (_helloForBusinessWatcher != null)
            {
                try
                {
                    _helloForBusinessWatcher.Enabled = false;
                    _helloForBusinessWatcher.EventRecordWritten -= OnHelloForBusinessEventRecordWritten;
                    _helloForBusinessWatcher.Dispose();
                    _helloForBusinessWatcher = null;
                }
                catch (Exception ex) { _logger.Error("Error stopping HelloForBusiness watcher", ex); }
            }
        }

        public void Dispose() => Stop();

        private void DisposeTimer(ref System.Threading.Timer timer, string name)
        {
            if (timer == null) return;
            try { timer.Dispose(); }
            catch (Exception ex) { _logger.Error($"Error stopping {name} timer", ex); }
            timer = null;
        }

        // =====================================================================
        // External coordination API (called by EspTracker / coordinator)
        // =====================================================================

        /// <summary>
        /// Force-marks Hello as completed from an external caller (e.g. safety timeout in EnrollmentTracker).
        /// Does NOT invoke the HelloCompleted event — the caller handles completion logic directly.
        /// </summary>
        public void ForceMarkHelloCompleted(string reason)
        {
            lock (_stateLock)
            {
                if (_isHelloCompleted) return;
                _isHelloCompleted = true;
                HelloOutcome = reason;
                StopHelloCompletionTimerLocked();
                _logger.Warning($"Hello force-completed by external caller: {reason}");
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
                _espExitSeen = false;
                HelloOutcome = null;
            }
            _logger.Info("HelloTracker: Reset for ESP resumption — Hello tracking restarted");
        }

        /// <summary>
        /// Starts the Hello wait timer. Called by EnrollmentTracker when AccountSetup phase exits.
        /// Waits for Hello wizard to start (Shell-Core event 62404, reported via
        /// <see cref="NotifyHelloWizardStarted"/>) within the configured timeout.
        /// If timeout expires without Hello wizard, marks Hello as completed so enrollment can proceed.
        /// </summary>
        public void StartHelloWaitTimer()
        {
            lock (_stateLock)
            {
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
                    TimeSpan.FromMilliseconds(-1));
            }
        }

        /// <summary>
        /// Called by the ESP tracker when Shell-Core event 62404 fires with AADHello/NGC context.
        /// Stops the wait timer (if running) and arms the long completion timer to catch cases
        /// where the wizard appeared but no terminal Hello event arrives.
        /// </summary>
        public void NotifyHelloWizardStarted()
        {
            lock (_stateLock)
            {
                if (_helloWizardStarted) return;
                _helloWizardStarted = true;

                if (_helloWaitTimer != null)
                {
                    _helloWaitTimer.Dispose();
                    _helloWaitTimer = null;
                    _logger.Info($"Hello wizard started within {_helloWaitTimeoutSeconds}s timeout - stopping wait timer");
                }

                StartHelloCompletionTimerLocked();
            }
        }

        /// <summary>
        /// Called by the ESP tracker when the ESP exits (Shell-Core 62407 with OOBE_ESP*Exiting).
        /// Used only for informational data fields in wait/completion timeout events.
        /// </summary>
        public void NotifyEspExited()
        {
            lock (_stateLock)
            {
                _espExitSeen = true;
            }
        }

        // =====================================================================
        // Policy detection (CSP + GPO)
        // =====================================================================

        private void CheckHelloPolicy()
        {
            try
            {
                var (isEnabled, source) = DetectHelloPolicy();

                lock (_stateLock)
                {
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

                        if (_policyCheckTimer != null)
                        {
                            _policyCheckTimer.Dispose();
                            _policyCheckTimer = null;
                            _logger.Debug("Stopped periodic Hello policy check - policy has been detected");
                        }
                    }
                    else if (!_isPolicyConfigured && !isEnabled.HasValue)
                    {
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

        // =====================================================================
        // UDR event log watcher (events 300/301/358/360/362/376)
        // =====================================================================

        private void StartUdrEventLogWatcher()
        {
            try
            {
                var query = new EventLogQuery(
                    UdrEventLogChannel,
                    PathType.LogName,
                    "*[System[(EventID=300 or EventID=301 or EventID=358 or EventID=360 or EventID=362 or EventID=376)]]");

                _udrWatcher = new EventLogWatcher(query);
                _udrWatcher.EventRecordWritten += OnUdrEventRecordWritten;
                _udrWatcher.Enabled = true;

                _logger.Info($"Started watching: {UdrEventLogChannel}");
            }
            catch (EventLogNotFoundException)
            {
                _logger.Warning($"Event log not found: {UdrEventLogChannel} (normal if not on a real device)");
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to start Hello event log watcher", ex);
            }
        }

        private void OnUdrEventRecordWritten(object sender, EventRecordWrittenEventArgs e)
        {
            if (e.EventRecord == null) return;

            try
            {
                var record = e.EventRecord;
                var eventId = record.Id;
                if (!TrackedUdrEventIds.Contains(eventId)) return;

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

        internal void ProcessHelloEvent(int eventId, DateTime timestamp, string providerName, bool isBackfill)
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

            // Keep payload minimal — full descriptions may contain sensitive data
            // (server responses with key IDs, UPNs, tokens).
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

        private void BackfillRecentTerminalHelloEvents()
        {
            try
            {
                var lookbackMs = BackfillLookbackMinutes * 60 * 1000;
                var query = new EventLogQuery(
                    UdrEventLogChannel,
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
                            if (timestamp < latestTimestamp) continue;

                            latestTimestamp = timestamp;
                            latestEventId = eventId;
                            latestProvider = record.ProviderName ?? "";
                        }
                    }

                    if (latestEventId.HasValue)
                    {
                        _logger.Info($"Backfill found recent Hello terminal event: EventID {latestEventId.Value} at {latestTimestamp:O}");
                        ProcessHelloEvent(
                            latestEventId.Value,
                            (latestTimestamp == DateTime.MinValue ? DateTime.UtcNow : latestTimestamp).ToUniversalTime(),
                            latestProvider,
                            isBackfill: true);
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

        // =====================================================================
        // HelloForBusiness/Operational watcher (events 3024/6045)
        // =====================================================================

        private void StartHelloForBusinessEventLogWatcher()
        {
            try
            {
                var query = new EventLogQuery(
                    HelloForBusinessEventLogChannel,
                    PathType.LogName,
                    "*[System[(EventID=3024 or EventID=6045)]]");

                _helloForBusinessWatcher = new EventLogWatcher(query);
                _helloForBusinessWatcher.EventRecordWritten += OnHelloForBusinessEventRecordWritten;
                _helloForBusinessWatcher.Enabled = true;

                _logger.Info($"Started watching: {HelloForBusinessEventLogChannel}");
            }
            catch (EventLogNotFoundException)
            {
                _logger.Warning($"Event log not found: {HelloForBusinessEventLogChannel} (normal if not on a real device)");
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to start HelloForBusiness event log watcher", ex);
            }
        }

        private void OnHelloForBusinessEventRecordWritten(object sender, EventRecordWrittenEventArgs e)
        {
            if (e.EventRecord == null) return;

            try
            {
                var record = e.EventRecord;
                var eventId = record.Id;
                if (!TrackedHelloForBusinessEventIds.Contains(eventId)) return;

                var description = record.FormatDescription() ?? $"Event ID {eventId}";
                var timestamp = (record.TimeCreated ?? DateTime.UtcNow).ToUniversalTime();

                ProcessHelloForBusinessEvent(eventId, timestamp, description, record.ProviderName ?? "", isBackfill: false);
            }
            catch (Exception ex)
            {
                _logger.Error("Error processing HelloForBusiness event record", ex);
            }
        }

        internal void ProcessHelloForBusinessEvent(int eventId, DateTime timestamp, string description, string providerName, bool isBackfill)
        {
            string eventType;
            EventSeverity severity;
            string message;
            bool shouldTriggerHelloCompleted = false;
            string hresult = null;

            switch (eventId)
            {
                case EventId_HelloForBusiness_ProcessingStarted: // 3024
                    eventType = "hello_processing_started";
                    severity = EventSeverity.Info;
                    message = "Windows Hello for Business processing started";
                    _logger.Info("Hello for Business processing started (event 3024)");
                    break;

                case EventId_HelloForBusiness_ProcessingStopped: // 6045
                    hresult = ExtractHResultFromDescription(description);

                    if (string.Equals(hresult, HResult_UserSkippedHello, StringComparison.OrdinalIgnoreCase))
                    {
                        eventType = "hello_skipped";
                        severity = EventSeverity.Warning;
                        message = $"Windows Hello for Business skipped by user ({HResult_UserSkippedHello})";
                        shouldTriggerHelloCompleted = MarkHelloSkipped();
                        _logger.Info($"Hello for Business SKIPPED by user (event 6045, HRESULT {hresult})");
                    }
                    else
                    {
                        eventType = "hello_processing_stopped";
                        severity = EventSeverity.Info;
                        message = $"Windows Hello for Business processing stopped (HRESULT: {hresult ?? "unknown"})";
                        _logger.Info($"Hello for Business processing stopped (event 6045, HRESULT {hresult ?? "unknown"}) - not treated as terminal");
                    }
                    break;

                default:
                    return;
            }

            var data = new Dictionary<string, object>
            {
                { "windowsEventId", eventId },
                { "providerName", providerName ?? "" },
                { "description", description },
                { "eventLogChannel", HelloForBusinessEventLogChannel },
                { "eventTime", timestamp.ToString("o") }
            };

            if (hresult != null) data["hresult"] = hresult;
            if (isBackfill) data["backfill"] = true;

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
                ImmediateUpload = shouldTriggerHelloCompleted
            });

            _logger.Info($"HelloForBusiness event detected: {eventType} (EventID {eventId}{(isBackfill ? ", backfill" : "")})");

            if (shouldTriggerHelloCompleted)
            {
                try { HelloCompleted?.Invoke(this, EventArgs.Empty); } catch { }
            }
        }

        private bool MarkHelloSkipped()
        {
            lock (_stateLock)
            {
                if (_isHelloCompleted)
                {
                    _logger.Debug("Hello skip event received but Hello is already marked completed");
                    return false;
                }

                _isHelloCompleted = true;
                HelloOutcome = "skipped";
                StopHelloCompletionTimerLocked();
                return true;
            }
        }

        internal static string ExtractHResultFromDescription(string description)
        {
            if (string.IsNullOrEmpty(description)) return null;
            var match = HResultPattern.Match(description);
            return match.Success ? match.Value : null;
        }

        private void BackfillRecentHelloForBusinessEvents()
        {
            try
            {
                var lookbackMs = BackfillLookbackMinutes * 60 * 1000;
                var query = new EventLogQuery(
                    HelloForBusinessEventLogChannel,
                    PathType.LogName,
                    $"*[System[(EventID=6045) and TimeCreated[timediff(@SystemTime) <= {lookbackMs}]]]");

                using (var reader = new EventLogReader(query))
                {
                    DateTime latestTimestamp = DateTime.MinValue;
                    string latestDescription = null;
                    string latestProvider = string.Empty;

                    for (EventRecord record = reader.ReadEvent(); record != null; record = reader.ReadEvent())
                    {
                        using (record)
                        {
                            var timestamp = record.TimeCreated ?? DateTime.MinValue;
                            if (timestamp < latestTimestamp) continue;

                            latestTimestamp = timestamp;
                            latestDescription = record.FormatDescription() ?? $"Event ID {record.Id}";
                            latestProvider = record.ProviderName ?? "";
                        }
                    }

                    if (latestDescription != null)
                    {
                        var hresult = ExtractHResultFromDescription(latestDescription);
                        if (string.Equals(hresult, HResult_UserSkippedHello, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.Info($"Backfill found recent Hello skip event: HRESULT {hresult} at {latestTimestamp:O}");
                            ProcessHelloForBusinessEvent(
                                EventId_HelloForBusiness_ProcessingStopped,
                                (latestTimestamp == DateTime.MinValue ? DateTime.UtcNow : latestTimestamp).ToUniversalTime(),
                                latestDescription,
                                latestProvider,
                                isBackfill: true);
                        }
                        else
                        {
                            _logger.Debug($"Backfill found HelloForBusiness event 6045 but HRESULT {hresult ?? "unknown"} is not a known terminal code - skipping");
                        }
                    }
                    else
                    {
                        _logger.Debug($"Backfill found no HelloForBusiness 6045 events in last {BackfillLookbackMinutes} minutes");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"HelloForBusiness event backfill failed: {ex.Message}");
            }
        }

        // =====================================================================
        // Timers: wait → completion
        // =====================================================================

        private void OnHelloWaitTimeout(object state)
        {
            lock (_stateLock)
            {
                if (_helloWizardStarted)
                {
                    _logger.Debug("Hello wait timeout fired but wizard already started - ignoring");
                    return;
                }

                if (_isHelloCompleted)
                {
                    _logger.Debug("Hello wait timeout fired but Hello already completed - ignoring");
                    return;
                }

                // Timeout expired without Hello wizard starting.
                // If Hello policy is known to be ENABLED, the wizard simply hasn't appeared yet
                // (e.g. Windows is still setting up prerequisites). Do NOT declare enrollment
                // complete — start the long HelloCompletion timer and keep waiting.
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
                            { "espExitDetected", _espExitSeen },
                            { "helloPolicyEnabled", true },
                            { "action", "extended_wait" }
                        },
                        ImmediateUpload = true
                    });

                    StartHelloCompletionTimerLocked();
                    return;
                }

                _logger.Info($"Hello wait timeout ({_helloWaitTimeoutSeconds}s) expired without Hello wizard starting");
                _logger.Info("Hello policy not detected as enabled — assuming Hello is not configured or was skipped");

                _isHelloCompleted = true;
                HelloOutcome = "not_configured";
                StopHelloCompletionTimerLocked();

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
                        { "espExitDetected", _espExitSeen },
                        { "helloPolicyEnabled", false },
                        { "action", "enrollment_complete" }
                    },
                    ImmediateUpload = true
                });

                try { HelloCompleted?.Invoke(this, EventArgs.Empty); }
                catch (Exception ex) { _logger.Error("Error invoking HelloCompleted from timeout", ex); }
            }
        }

        private void StartHelloCompletionTimerLocked()
        {
            if (_isHelloCompleted) return;
            if (_helloCompletionTimer != null) return;

            _logger.Info($"Starting Hello completion timer ({HelloCompletionTimeoutSeconds}s) - waiting for terminal Hello event (300/301/362)");
            _helloCompletionTimer = new System.Threading.Timer(
                OnHelloCompletionTimeout,
                null,
                TimeSpan.FromSeconds(HelloCompletionTimeoutSeconds),
                TimeSpan.FromMilliseconds(-1));
        }

        private void StopHelloCompletionTimerLocked()
        {
            if (_helloCompletionTimer == null) return;
            try { _helloCompletionTimer.Dispose(); } catch { }
            finally { _helloCompletionTimer = null; }
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

                try { HelloCompleted?.Invoke(this, EventArgs.Empty); }
                catch (Exception ex) { _logger.Error("Error invoking HelloCompleted from completion timeout", ex); }
            }
        }

        // =====================================================================
        // Test seams — allow tests to drive timer logic deterministically
        // =====================================================================

        /// <summary>Test-only: simulate policy detection without a real registry read.</summary>
        internal void SetPolicyForTest(bool helloEnabled, string source)
        {
            lock (_stateLock)
            {
                _isPolicyConfigured = true;
                _isHelloPolicyEnabled = helloEnabled;

                _onEventCollected(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId = _tenantId,
                    EventType = "hello_policy_detected",
                    Severity = EventSeverity.Info,
                    Source = "EspAndHelloTracker",
                    Phase = EnrollmentPhase.Unknown,
                    Message = $"Windows Hello for Business policy detected: {(helloEnabled ? "enabled" : "disabled")} (via {source})",
                    Data = new Dictionary<string, object>
                    {
                        { "helloEnabled", helloEnabled },
                        { "policySource", source }
                    }
                });
            }
        }

        internal void TriggerWaitTimeoutForTest() => OnHelloWaitTimeout(null);
        internal void TriggerCompletionTimeoutForTest() => OnHelloCompletionTimeout(null);

        internal bool IsWaitTimerActiveForTest { get { lock (_stateLock) { return _helloWaitTimer != null; } } }
        internal bool IsCompletionTimerActiveForTest { get { lock (_stateLock) { return _helloCompletionTimer != null; } } }
        internal bool IsHelloWizardStartedForTest { get { lock (_stateLock) { return _helloWizardStarted; } } }
    }
}
