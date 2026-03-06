using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.Core.Monitoring.Collectors
{
    /// <summary>
    /// Partial: Shell-Core event log handling — ESP exit detection, ESP failure detection,
    /// WhiteGlove detection, Hello wizard start detection, and ESP backfill.
    /// </summary>
    public partial class EspAndHelloTracker
    {
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
                            var failureType = ExtractEspFailureType(description);
                            eventType = "esp_failure";
                            severity = EventSeverity.Error;
                            message = $"ESP (Enrollment Status Page) reported a failure: {failureType}";
                            _logger.Warning($"ESP failure detected via Shell-Core event 62407: {failureType}");
                            _detectedEspFailureType = failureType;
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

                var eventTimestamp = (record.TimeCreated ?? DateTime.UtcNow).ToUniversalTime();

                var eventData = new Dictionary<string, object>
                {
                    { "windowsEventId", eventId },
                    { "providerName", record.ProviderName ?? "" },
                    { "description", description },
                    { "eventLogChannel", ShellCoreEventLogChannel },
                    { "eventTime", eventTimestamp.ToString("o") }
                };

                if (eventType == "esp_failure" && _detectedEspFailureType != null)
                {
                    eventData["failureType"] = _detectedEspFailureType;
                }

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
                    Data = eventData
                });

                _logger.Info($"Shell-Core event detected: {eventType} (EventID {eventId})");

                // Trigger FinalizingSetup phase transition
                if (triggerFinalizingSetup)
                {
                    try
                    {
                        FinalizingSetupPhaseTriggered?.Invoke(this, finalizingSetupReason);
                    }
                    catch (Exception ex) { _logger.Error("FinalizingSetupPhaseTriggered handler failed", ex); }
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
                    catch (Exception ex) { _logger.Error("WhiteGloveCompleted handler failed", ex); }
                }

                // Fire EspFailureDetected if this was an ESP failure event.
                // Must happen AFTER the event has been emitted above so the
                // esp_failure event is in the spool before the agent potentially shuts down.
                if (eventType == "esp_failure" && _detectedEspFailureType != null)
                {
                    var failureType = _detectedEspFailureType;
                    _detectedEspFailureType = null;
                    try
                    {
                        EspFailureDetected?.Invoke(this, failureType);
                    }
                    catch (Exception ex) { _logger.Error($"EspFailureDetected handler failed for '{failureType}'", ex); }
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Error processing Shell-Core event record", ex);
            }
        }

        /// <summary>
        /// Extracts a structured failure type from the Shell-Core event description.
        /// E.g. "ESPProgress_Failure", "ESPProgress_Timeout", "WhiteGlove_Failed".
        /// </summary>
        private static string ExtractEspFailureType(string description)
        {
            // Known failure type keywords to search for in order of specificity
            string[] knownTypes = {
                "ESPProgress_Failure",
                "ESPProgress_Failed",
                "ESPProgress_Timeout",
                "ESPProgress_Abort",
                "WhiteGlove_Failed",
                "WhiteGlove_Failure"
            };

            foreach (var type in knownTypes)
            {
                if (description.Contains(type, StringComparison.OrdinalIgnoreCase))
                    return type;
            }

            return "Unknown_ESP_Failure";
        }

        /// <summary>
        /// Backfills recent ESP exit and failure events from Shell-Core log on startup.
        /// Secondary recovery mechanism when state persistence is unavailable.
        /// </summary>
        public void BackfillRecentEspExitEvents()
        {
            try
            {
                var lookbackMs = BackfillLookbackMinutes * 60 * 1000;
                var query = new EventLogQuery(
                    ShellCoreEventLogChannel,
                    PathType.LogName,
                    $"*[System[(EventID=62407) and TimeCreated[timediff(@SystemTime) <= {lookbackMs}]]]");

                using (var reader = new EventLogReader(query))
                {
                    for (EventRecord record = reader.ReadEvent(); record != null; record = reader.ReadEvent())
                    {
                        using (record)
                        {
                            var description = record.FormatDescription() ?? "";

                            // Check for ESP exit
                            if (System.Text.RegularExpressions.Regex.IsMatch(description, @"OOBE_ESP.*Exiting", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                            {
                                lock (_stateLock)
                                {
                                    if (!_espExitDetected)
                                    {
                                        _espExitDetected = true;
                                        _logger.Info("Backfill: ESP exit event found in recent Shell-Core logs");
                                        try { FinalizingSetupPhaseTriggered?.Invoke(this, "esp_exiting"); } catch (Exception ex) { _logger.Error("Backfill: FinalizingSetupPhaseTriggered handler failed", ex); }
                                    }
                                }
                            }

                            // Check for ESP failures
                            if (description.Contains("ESPProgress_Failure", StringComparison.OrdinalIgnoreCase)
                                || description.Contains("ESPProgress_Failed", StringComparison.OrdinalIgnoreCase)
                                || description.Contains("ESPProgress_Timeout", StringComparison.OrdinalIgnoreCase)
                                || description.Contains("ESPProgress_Abort", StringComparison.OrdinalIgnoreCase)
                                || description.Contains("WhiteGlove_Failed", StringComparison.OrdinalIgnoreCase)
                                || description.Contains("WhiteGlove_Failure", StringComparison.OrdinalIgnoreCase))
                            {
                                var failureType = ExtractEspFailureType(description);
                                _logger.Info($"Backfill: ESP failure event found in recent Shell-Core logs: {failureType}");
                                try { EspFailureDetected?.Invoke(this, failureType); } catch (Exception ex) { _logger.Error($"Backfill: EspFailureDetected handler failed for '{failureType}'", ex); }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"ESP exit/failure event backfill failed: {ex.Message}");
            }
        }
    }
}
