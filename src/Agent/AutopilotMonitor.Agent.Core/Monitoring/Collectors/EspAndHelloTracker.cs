using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.Core.Monitoring.Collectors
{
    /// <summary>
    /// Tracks ESP (Enrollment Status Page) exit events and Windows Hello for Business (WHfB)
    /// provisioning during Autopilot enrollment, including WhiteGlove (Pre-Provisioning) detection.
    ///
    /// Key Event IDs (User Device Registration/Admin):
    ///   358 - Prerequisites passed, provisioning will be launched
    ///   360 - Prerequisites failed, provisioning will NOT be launched (SNAPSHOT ONLY - not terminal)
    ///   362 - Provisioning blocked
    ///   300 - NGC key registered successfully (Hello provisioned)
    ///   301 - NGC key registration failed
    ///
    /// Key Event IDs (Microsoft-Windows-Shell-Core/Operational):
    ///   62404 - CloudExperienceHost Web App Activity Started (CXID: 'AADHello' or 'NGC' - Hello wizard started)
    ///   62407 - CloudExperienceHost Web App Event 2:
    ///           CommercialOOBE_ESPProgress_Page_Exiting      — normal ESP exit
    ///           CommercialOOBE_ESPProgress_WhiteGlove_Success — WhiteGlove (Pre-Provisioning) complete
    /// </summary>
    public class EspAndHelloTracker : IDisposable
    {
        private readonly AgentLogger _logger;
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly Action<EnrollmentEvent> _onEventCollected;
        private System.Diagnostics.Eventing.Reader.EventLogWatcher _watcher;
        private System.Diagnostics.Eventing.Reader.EventLogWatcher _shellCoreWatcher;
        private System.Threading.Timer _policyCheckTimer;
        private System.Threading.Timer _helloWaitTimer;
        private System.Threading.Timer _helloCompletionTimer;

        private bool _isPolicyConfigured = false;
        private bool _isHelloPolicyEnabled = false; // true when Hello policy is explicitly detected as enabled
        private bool _isHelloCompleted = false;
        private bool _espExitDetected = false;
        private bool _helloWizardStarted = false;
        private bool _whiteGloveDetected = false;
        private readonly object _stateLock = new object();

        private readonly int _helloWaitTimeoutSeconds;
        private const int HelloCompletionTimeoutSeconds = 1500;
        private const int BackfillLookbackMinutes = 5;

        /// <summary>
        /// Callback invoked when Hello provisioning completes (successfully or failed)
        /// Based on events 300/301 only, NOT on event 360 (which is just a snapshot)
        /// </summary>
        public event EventHandler HelloCompleted;

        /// <summary>
        /// Callback invoked when ESP exit or Hello wizard start is detected
        /// Triggers transition to FinalizingSetup phase
        /// </summary>
        public event EventHandler<string> FinalizingSetupPhaseTriggered;

        /// <summary>
        /// Fired when WhiteGlove (Pre-Provisioning) completes successfully.
        /// The device will shut down; the agent should terminate gracefully.
        /// </summary>
        public event EventHandler WhiteGloveCompleted;

        private const string EventLogChannel = "Microsoft-Windows-User Device Registration/Admin";
        private const string ShellCoreEventLogChannel = "Microsoft-Windows-Shell-Core/Operational";

        // WHfB policy registry paths
        private const string CspPolicyBasePath = @"SOFTWARE\Microsoft\Policies\PassportForWork";
        private const string GpoPolicyPath = @"SOFTWARE\Policies\Microsoft\PassportForWork";

        // Tracked event IDs (User Device Registration)
        private const int EventId_NgcKeyRegistered = 300;
        private const int EventId_NgcKeyRegistrationFailed = 301;
        private const int EventId_ProvisioningWillLaunch = 358;
        private const int EventId_ProvisioningWillNotLaunch = 360;
        private const int EventId_ProvisioningBlocked = 362;
        private const int EventId_PinStatus = 376;

        // Tracked event IDs (Shell-Core/Operational)
        private const int EventId_ShellCore_WebAppStarted = 62404;
        private const int EventId_ShellCore_WebAppEvent = 62407;

        private static readonly HashSet<int> TrackedEventIds = new HashSet<int>
        {
            EventId_NgcKeyRegistered,
            EventId_NgcKeyRegistrationFailed,
            EventId_ProvisioningWillLaunch,
            EventId_ProvisioningWillNotLaunch,
            EventId_ProvisioningBlocked,
            EventId_PinStatus
        };

        private static readonly HashSet<int> TrackedShellCoreEventIds = new HashSet<int>
        {
            EventId_ShellCore_WebAppStarted,
            EventId_ShellCore_WebAppEvent
        };

        public EspAndHelloTracker(string sessionId, string tenantId, Action<EnrollmentEvent> onEventCollected, AgentLogger logger, int helloWaitTimeoutSeconds = 30)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _onEventCollected = onEventCollected ?? throw new ArgumentNullException(nameof(onEventCollected));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _helloWaitTimeoutSeconds = helloWaitTimeoutSeconds;
        }

        /// <summary>
        /// Gets whether Windows Hello for Business policy is configured (enabled or disabled)
        /// </summary>
        public bool IsPolicyConfigured
        {
            get { lock (_stateLock) { return _isPolicyConfigured; } }
        }

        /// <summary>
        /// Gets whether Windows Hello provisioning has completed (successfully, failed, or skipped)
        /// </summary>
        public bool IsHelloCompleted
        {
            get { lock (_stateLock) { return _isHelloCompleted; } }
        }

        public void Start()
        {
            _logger.Info("Starting ESP and Hello tracker");

            // Check if WHfB policy is configured initially
            CheckHelloPolicy();

            // Start periodic policy check to detect policy arriving later via MDM
            _policyCheckTimer = new System.Threading.Timer(
                _ => CheckHelloPolicy(),
                null,
                TimeSpan.FromSeconds(10), // Initial delay before first check
                TimeSpan.FromSeconds(10)  // Subsequent checks every 10 seconds (fast detection, low cost)
            );

            // Subscribe to User Device Registration event log
            StartEventLogWatcher();

            // Subscribe to Shell-Core/Operational event log for ESP exit and Hello wizard detection
            StartShellCoreEventLogWatcher();

            // Safety net: backfill recent terminal events in case watcher started late or event delivery lagged.
            BackfillRecentTerminalHelloEvents();
        }

        public void Stop()
        {
            _logger.Info("Stopping ESP and Hello tracker");

            // Stop periodic policy check timer
            if (_policyCheckTimer != null)
            {
                try
                {
                    _policyCheckTimer.Dispose();
                    _policyCheckTimer = null;
                }
                catch (Exception ex)
                {
                    _logger.Error("Error stopping ESP and Hello tracker policy check timer", ex);
                }
            }

            // Stop Hello wait timer
            if (_helloWaitTimer != null)
            {
                try
                {
                    _helloWaitTimer.Dispose();
                    _helloWaitTimer = null;
                }
                catch (Exception ex)
                {
                    _logger.Error("Error stopping Hello wait timer", ex);
                }
            }

            // Stop Hello completion timer
            if (_helloCompletionTimer != null)
            {
                try
                {
                    _helloCompletionTimer.Dispose();
                    _helloCompletionTimer = null;
                }
                catch (Exception ex)
                {
                    _logger.Error("Error stopping Hello completion timer", ex);
                }
            }

            // Stop User Device Registration watcher
            if (_watcher != null)
            {
                try
                {
                    _watcher.Enabled = false;
                    _watcher.EventRecordWritten -= OnEventRecordWritten;
                    _watcher.Dispose();
                    _watcher = null;
                }
                catch (Exception ex)
                {
                    _logger.Error("Error stopping ESP and Hello tracker watcher", ex);
                }
            }

            // Stop Shell-Core/Operational watcher
            if (_shellCoreWatcher != null)
            {
                try
                {
                    _shellCoreWatcher.Enabled = false;
                    _shellCoreWatcher.EventRecordWritten -= OnShellCoreEventRecordWritten;
                    _shellCoreWatcher.Dispose();
                    _shellCoreWatcher = null;
                }
                catch (Exception ex)
                {
                    _logger.Error("Error stopping Shell-Core event watcher", ex);
                }
            }
        }

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
                    record.TimeCreated ?? DateTime.UtcNow,
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
                Data = data
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
                StopHelloCompletionTimerLocked();
                return true;
            }
        }

        private void StartShellCoreEventLogWatcher()
        {
            try
            {
                // Watch for ESP exit and Hello wizard start events in Shell-Core/Operational log
                var query = new EventLogQuery(
                    ShellCoreEventLogChannel,
                    PathType.LogName,
                    "*[System[(EventID=62404 or EventID=62407)]]"
                );

                _shellCoreWatcher = new System.Diagnostics.Eventing.Reader.EventLogWatcher(query);
                _shellCoreWatcher.EventRecordWritten += OnShellCoreEventRecordWritten;
                _shellCoreWatcher.Enabled = true;

                _logger.Info($"Started watching: {ShellCoreEventLogChannel}");
            }
            catch (EventLogNotFoundException)
            {
                _logger.Warning($"Event log not found: {ShellCoreEventLogChannel} (normal if not on a real device)");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to start Shell-Core event log watcher", ex);
            }
        }

        private void OnShellCoreEventRecordWritten(object sender, EventRecordWrittenEventArgs e)
        {
            if (e.EventRecord == null)
                return;

            try
            {
                var record = e.EventRecord;
                var eventId = record.Id;

                if (!TrackedShellCoreEventIds.Contains(eventId))
                    return;

                var description = record.FormatDescription() ?? $"Event ID {eventId}";

                string eventType;
                EventSeverity severity = EventSeverity.Info;
                string message;
                bool triggerFinalizingSetup = false;
                string finalizingSetupReason = null;

                switch (eventId)
                {
                    case EventId_ShellCore_WebAppStarted: // 62404
                        // Check if this is AADHello or NGC (Hello wizard started)
                        if (description.Contains("AADHello") || description.Contains("'NGC'"))
                        {
                            eventType = "hello_wizard_started";
                            message = "Windows Hello wizard started (CloudExperienceHost)";
                            triggerFinalizingSetup = true;
                            finalizingSetupReason = "hello_wizard_started";

                            lock (_stateLock)
                            {
                                _helloWizardStarted = true;

                                // Stop the hello wait timer if running (Hello wizard appeared within timeout)
                                if (_helloWaitTimer != null)
                                {
                                    _helloWaitTimer.Dispose();
                                    _helloWaitTimer = null;
                                    _logger.Info($"Hello wizard started within {_helloWaitTimeoutSeconds}s timeout - stopping wait timer");
                                }

                                StartHelloCompletionTimerLocked();
                            }

                            _logger.Info("Windows Hello wizard started - detected via Shell-Core event 62404");
                        }
                        else
                        {
                            // Not Hello-related, ignore
                            return;
                        }
                        break;

                    //{
                    //"windowsEventId": 62407,
                    //"providerName": "Microsoft-Windows-Shell-Core",
                    //"description": "CloudExperienceHost Web App Event 2. Name: 'CommercialOOBE_ESP_ExitPage', Value: '{\"message\":\"BootstrapStatus: Clearing ESP cache on page exit\",\"errorCode\":0}'.",
                    //"eventLogChannel": "Microsoft-Windows-Shell-Core/Operational"
                    //}

                    //{
                    //"windowsEventId": 62407,
                    //"providerName": "Microsoft-Windows-Shell-Core",
                    //"description": "CloudExperienceHost Web App Event 2. Name: 'CommercialOOBE_ESPProgress_Page_Exiting', Value: '{\"message\":\"BootstrapStatus: Exiting page normally.\",\"errorCode\":0}'.",
                    //"eventLogChannel": "Microsoft-Windows-Shell-Core/Operational"
                    //}

                    //{
                    //"windowsEventId": 62407,
                    //"providerName": "Microsoft-Windows-Shell-Core",
                    //"description": "CloudExperienceHost-Web-App-Ereignis 2. Name: 'CommercialOOBE_ESPProgress_WhiteGlove_Success', Wert: '{\"message\":\"BootstrapStatus: Exiting page due to White Glove success.\",\"errorCode\":0}'.",
                    //"eventLogChannel": "Microsoft-Windows-Shell-Core/Operational"
                    //}

                    //{
                    //"windowsEventId": 62407,
                    //"providerName": "Microsoft-Windows-Shell-Core",
                    //"description": "CloudExperienceHost Web App Event 2. Name: 'CommercialOOBE_ESPProgress_Failure', Value: '{\"message\":\"BootstrapStatus: ...\",\"errorCode\":...}'.",
                    //"eventLogChannel": "Microsoft-Windows-Shell-Core/Operational"
                    //}

                    case EventId_ShellCore_WebAppEvent: // 62407
                        // WhiteGlove check FIRST — its description also contains "Exiting"
                        // which would match the generic OOBE_ESP.*Exiting regex below.
                        if (description.Contains("WhiteGlove_Success", StringComparison.OrdinalIgnoreCase))
                        {
                            // Guard: event 62407 can fire multiple times; only process once
                            lock (_stateLock)
                            {
                                if (_whiteGloveDetected) return;
                                _whiteGloveDetected = true;
                            }

                            eventType = "whiteglove_complete";
                            message = "WhiteGlove (Pre-Provisioning) completed successfully";
                            // Do NOT set triggerFinalizingSetup — WhiteGlove terminates the
                            // pre-provisioning phase entirely, it does not transition to FinalizingSetup.

                            _logger.Info("WhiteGlove (Pre-Provisioning) success detected via Shell-Core event 62407");
                        }
                        // Check if this is ESP exit event
                        // Use robust pattern: OOBE_ESP*Exiting* instead of full string CommercialOOBE_ESPProgress_Page_Exiting
                        // Fix 26.02.26 - RegEx was not preceisely matching as we used Exit instead of Exiting, which is the actual value in the event description.
                        //                Updated to check for Exiting to reliably detect ESP exit events. Compare with event samples from real devices listed above.
                        else if (description.Contains("ESPProgress_Failure", StringComparison.OrdinalIgnoreCase)
                              || description.Contains("ESPProgress_Failed", StringComparison.OrdinalIgnoreCase)
                              || description.Contains("ESPProgress_Timeout", StringComparison.OrdinalIgnoreCase)
                              || description.Contains("ESPProgress_Abort", StringComparison.OrdinalIgnoreCase)
                              || description.Contains("WhiteGlove_Failed", StringComparison.OrdinalIgnoreCase)
                              || description.Contains("WhiteGlove_Failure", StringComparison.OrdinalIgnoreCase))
                        {
                            eventType = "esp_failure";
                            severity = EventSeverity.Error;
                            message = "ESP (Enrollment Status Page) reported a failure";
                            _logger.Warning("ESP failure detected via Shell-Core event 62407");
                        }
                        else if (System.Text.RegularExpressions.Regex.IsMatch(description, @"OOBE_ESP.*Exiting", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        {
                            eventType = "esp_exiting";
                            message = "ESP (Enrollment Status Page) phase exiting";
                            triggerFinalizingSetup = true;
                            finalizingSetupReason = "esp_exiting";

                            lock (_stateLock)
                            {
                                _espExitDetected = true;

                                // NOTE: We do NOT start the Hello wait timer here!
                                // Event 62407 occurs at every ESP phase transition (Device->Account, Account->End)
                                // EnrollmentTracker will decide based on _lastEspPhase whether to start the timer
                            }

                            _logger.Info("ESP phase exit detected - detected via Shell-Core event 62407");
                        }
                        else
                        {
                            // Not a tracked ESP event, ignore
                            return;
                        }
                        break;

                    default:
                        return;
                }

                var eventTimestamp = record.TimeCreated ?? DateTime.UtcNow;

                _onEventCollected(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId = _tenantId,
                    Timestamp = eventTimestamp,
                    EventType = eventType,
                    Severity = severity,
                    Source = "EspAndHelloTracker",
                    Phase = EnrollmentPhase.Unknown, // Let EnrollmentTracker decide phase
                    Message = message,
                    Data = new Dictionary<string, object>
                    {
                        { "windowsEventId", eventId },
                        { "providerName", record.ProviderName ?? "" },
                        { "description", description },
                        { "eventLogChannel", ShellCoreEventLogChannel },
                        { "eventTime", eventTimestamp.ToString("o") }
                    }
                });

                _logger.Info($"Shell-Core event detected: {eventType} (EventID {eventId})");

                // Trigger FinalizingSetup phase transition
                if (triggerFinalizingSetup)
                {
                    try
                    {
                        FinalizingSetupPhaseTriggered?.Invoke(this, finalizingSetupReason);
                    }
                    catch { }
                }

                // Fire WhiteGloveCompleted if this was a WhiteGlove success event.
                // Must happen AFTER the event has been emitted above so the
                // whiteglove_complete event is in the spool before the agent exits.
                if (eventType == "whiteglove_complete")
                {
                    try
                    {
                        WhiteGloveCompleted?.Invoke(this, EventArgs.Empty);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Error processing Shell-Core event record", ex);
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
                        }
                    });

                    // Arm the long completion timer so we don't wait forever
                    StartHelloCompletionTimerLocked();
                    return;
                }

                // Hello policy disabled or unknown — assume Hello is not configured
                _logger.Info($"Hello wait timeout ({_helloWaitTimeoutSeconds}s) expired without Hello wizard starting");
                _logger.Info("Hello policy not detected as enabled — assuming Hello is not configured or was skipped");

                _isHelloCompleted = true;
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
                    }
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
                    }
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
                        ProcessHelloEvent(latestEventId.Value, latestTimestamp == DateTime.MinValue ? DateTime.UtcNow : latestTimestamp, latestProvider, isBackfill: true);
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

        public void Dispose()
        {
            Stop();
        }
    }
}
