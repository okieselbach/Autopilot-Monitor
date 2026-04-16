using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.Core.Monitoring.Enrollment
{
    /// <summary>
    /// Partial: IME log event handlers (ESP phase, app state, script completion, etc.).
    /// </summary>
    public partial class EnrollmentTracker
    {
        /// <summary>
        /// Wraps _emitEvent and automatically injects the current ImeLogTracker patternId into event Data.
        /// Use this for all events emitted from ImeLogTracker callback handlers.
        /// </summary>
        private void EmitImeTrackerEvent(EnrollmentEvent evt)
        {
            var logTimestamp = _imeLogTracker?.LastMatchedLogTimestamp;
            if (logTimestamp.HasValue)
                evt.Timestamp = logTimestamp.Value;

            var patternId = _imeLogTracker?.LastMatchedPatternId;
            if (!string.IsNullOrEmpty(patternId))
            {
                if (evt.Data == null)
                    evt.Data = new Dictionary<string, object>();
                evt.Data["patternId"] = patternId;
            }
            _emitEvent(evt);
        }

        private void HandleEspPhaseChanged(string phase)
        {
            string lastPhase;
            bool tracksEspPhases;
            lock (_stateLock)
            {
                tracksEspPhases = _flowHandler.TracksEspPhases;
                lastPhase = _lastEspPhase;
            }

            // Flows that do not track ESP phases (DevPrep/WDP) ignore these events entirely.
            if (!tracksEspPhases)
            {
                _logger.Debug($"EnrollmentTracker: skipping ESP phase event — flow does not track ESP (phase: {phase})");
                return;
            }

            // ESP resumed after final exit — only possible during hybrid join reboot recovery.
            // In hybrid join, ESP exits for a mid-enrollment reboot (domain user login required),
            // then the same phase re-appears in IME after the agent restarts. Reset completion state
            // so the agent waits for the real final exit instead of self-destructing prematurely.
            // Non-hybrid scenarios can see the same phase re-reported by IME (e.g., during user
            // session completion) which is NOT a real resumption — skip the reset entirely.
            bool espFinalExitSeen;
            lock (_stateLock) { espFinalExitSeen = _espFinalExitSeen; }

            if (_isHybridJoin && espFinalExitSeen && string.Equals(phase, lastPhase, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Info($"EnrollmentTracker: ESP phase '{phase}' re-detected after final exit — " +
                             "ESP has resumed (hybrid join reboot recovery), resetting completion state");
                lock (_stateLock)
                {
                    _espFinalExitSeen = false;
                    _stateData.EspFinalExitSeen = false;
                    _stateData.EspFinalExitUtc = null;
                }
                _statePersistence.Save(_stateData); // Immediate persist — ESP resumed resets final exit, must survive crash
                RecordSignal("esp_resumed");
                _espAndHelloTracker?.ResetForEspResumption();

                _emitEvent(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId = _tenantId,
                    EventType = "esp_resumed",
                    Severity = EventSeverity.Info,
                    Source = "EnrollmentTracker",
                    Phase = EnrollmentPhase.AccountSetup,
                    Message = $"ESP phase '{phase}' re-detected after final exit — enrollment resumed after reboot",
                    Data = new Dictionary<string, object>
                    {
                        { "espPhase", phase },
                        { "isHybridJoin", _isHybridJoin }
                    },
                    ImmediateUpload = true
                });

                // Shadow state machine: track ESP resumed
                ShadowProcessTrigger("esp_resumed");
                return;
            }

            // Only emit event if the phase has actually changed
            if (string.Equals(phase, lastPhase, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Debug($"EnrollmentTracker: ESP phase unchanged ({phase}), skipping event");
                return;
            }

            // AccountSetup verification: during WhiteGlove pre-provisioning the ESP briefly enters
            // AccountSetup before WhiteGlove success, but no real user has signed in (only defaultuser0
            // exists). Suppress the phase event to avoid a fake AccountSetup on the timeline.
            // The ImeLogTracker has already updated its internal phase tracking (ignore lists, phase order)
            // which is correct — we only suppress the event emission to the backend.
            if (string.Equals(phase, "AccountSetup", StringComparison.OrdinalIgnoreCase))
            {
                var hasReal = HasRealUserProfile();
                _logger.Debug($"EnrollmentTracker: AccountSetup verification — HasRealUserProfile={hasReal}");
                if (!hasReal)
                {
                    _logger.Info("EnrollmentTracker: AccountSetup suppressed — no real user profile (likely WhiteGlove)");
                    EmitTraceEvent("AccountSetup_suppressed",
                        "ESP reported AccountSetup but no real user profile found — likely WhiteGlove pre-provisioning",
                        new Dictionary<string, object> { { "espPhase", phase }, { "previousPhase", lastPhase ?? "null" } });
                    return;
                }
            }

            _logger.Info($"EnrollmentTracker: ESP phase changed from '{lastPhase ?? "null"}' to '{phase}'");
            lock (_stateLock)
            {
                _lastEspPhase = phase;
                _hasAutoSwitchedToAppsPhase = false; // Reset when ESP phase changes
                _espEverSeen = true;
                _stateData.EspEverSeen = true;
                _stateData.LastEspPhase = phase;
                if (_stateData.EspFirstSeenUtc == null)
                    _stateData.EspFirstSeenUtc = DateTime.UtcNow;
            }
            _statePersistence.Save(_stateData); // Immediate persist — ESP phase + espEverSeen must survive reboot
            RecordSignal($"esp_phase_{phase}");

            // ESP phase change means ESP is progressing — cancel any pending failure grace period
            CancelPendingEspFailure();

            // Cancel device-only ESP timer if AccountSetup phase detected
            if (string.Equals(phase, "AccountSetup", StringComparison.OrdinalIgnoreCase))
            {
                if (_deviceOnlyEspTimer != null)
                {
                    _logger.Info("EnrollmentTracker: AccountSetup detected — cancelling device-only ESP timer");
                    _deviceOnlyEspTimer.Dispose();
                    _deviceOnlyEspTimer = null;
                }
            }

            // Map ESP phase to EnrollmentPhase (phase change events)
            var enrollmentPhase = EnrollmentPhase.DeviceSetup;
            if (string.Equals(phase, "AccountSetup", StringComparison.OrdinalIgnoreCase))
                enrollmentPhase = EnrollmentPhase.AccountSetup;

            EmitImeTrackerEvent(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = "esp_phase_changed",
                Severity = EventSeverity.Info,
                Source = "ImeLogTracker",
                Phase = enrollmentPhase,
                Message = $"ESP phase: {phase}",
                Data = new Dictionary<string, object> { { "espPhase", phase } },
                ImmediateUpload = true
            });

            // Re-collect enrollment-dependent device info (AAD join, profile, ESP config)
            // that may have been empty at startup (bootstrap / pre-enrollment scenario).
            CollectDeviceInfoAtEnrollmentStart();

            // Check for ConfigMgr client at phase transitions (may arrive after agent startup)
            Task.Run(() => CheckConfigMgrClient(
                enrollmentPhase == EnrollmentPhase.AccountSetup ? "account_setup" : "device_setup"));

            // Start summary timer when we detect ESP phase
            if (!_summaryTimerActive)
            {
                _summaryTimerActive = true;
                _summaryTimer?.Change(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            }
            if (!_debugStateTimerActive)
            {
                _debugStateTimerActive = true;
                _debugStateTimer?.Change(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
            }

            // Shadow state machine: track ESP phase change
            ShadowProcessTrigger("esp_phase_changed");
        }

        private void HandleImeAgentVersion(string version)
        {
            EmitImeTrackerEvent(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = "ime_agent_version",
                Severity = EventSeverity.Info,
                Source = "ImeLogTracker",
                Phase = EnrollmentPhase.Unknown,
                Message = $"IME Agent version: {version}",
                Data = new Dictionary<string, object> { { "agentVersion", version } }
            });
        }

        private void HandleImeStarted()
        {
            _logger.Info("EnrollmentTracker: IME started event");
        }

        private void HandleAppStateChanged(AppPackageState app, AppInstallationState oldState, AppInstallationState newState)
        {
            _logger.Verbose($"EnrollmentTracker: app state change '{app.Name ?? app.Id}': {oldState} -> {newState}");

            // Auto-switch to app installation phase when first app activity detected
            // If we're in DeviceSetup and an app starts downloading/installing, switch to AppsDevice
            // If we're in AccountSetup and an app starts downloading/installing, switch to AppsUser
            bool hasAutoSwitched;
            string currentPhase;
            lock (_stateLock)
            {
                hasAutoSwitched = _hasAutoSwitchedToAppsPhase;
                currentPhase = _lastEspPhase;
            }

            if (!hasAutoSwitched &&
                (newState == AppInstallationState.Downloading || newState == AppInstallationState.Installing) &&
                oldState < AppInstallationState.Downloading)
            {
                if (currentPhase != null)
                {
                    if (string.Equals(currentPhase, "DeviceSetup", StringComparison.OrdinalIgnoreCase))
                    {
                        // Switch from DeviceSetup to AppsDevice
                        _logger.Info($"EnrollmentTracker: First app activity detected during DeviceSetup, switching to AppsDevice phase");
                        lock (_stateLock) { _hasAutoSwitchedToAppsPhase = true; }
                        EmitImeTrackerEvent(new EnrollmentEvent
                        {
                            SessionId = _sessionId,
                            TenantId = _tenantId,
                            EventType = "esp_phase_changed",
                            Severity = EventSeverity.Info,
                            Source = "EnrollmentTracker",
                            Phase = EnrollmentPhase.AppsDevice,
                            Message = "ESP phase: AppsDevice (auto-detected from app activity)",
                            Data = new Dictionary<string, object> { { "espPhase", "AppsDevice" }, { "autoDetected", true } },
                            ImmediateUpload = true
                        });
                    }
                    else if (string.Equals(currentPhase, "AccountSetup", StringComparison.OrdinalIgnoreCase))
                    {
                        // Switch from AccountSetup to AppsUser
                        _logger.Info($"EnrollmentTracker: First app activity detected during AccountSetup, switching to AppsUser phase");
                        lock (_stateLock) { _hasAutoSwitchedToAppsPhase = true; }
                        EmitImeTrackerEvent(new EnrollmentEvent
                        {
                            SessionId = _sessionId,
                            TenantId = _tenantId,
                            EventType = "esp_phase_changed",
                            Severity = EventSeverity.Info,
                            Source = "EnrollmentTracker",
                            Phase = EnrollmentPhase.AppsUser,
                            Message = "ESP phase: AppsUser (auto-detected from app activity)",
                            Data = new Dictionary<string, object> { { "espPhase", "AppsUser" }, { "autoDetected", true } },
                            ImmediateUpload = true
                        });
                    }
                    else if (string.Equals(currentPhase, "FinalizingSetup", StringComparison.OrdinalIgnoreCase))
                    {
                        // SkipUserStatusPage flow: apps starting after FinalizingSetup are background user apps
                        _logger.Info($"EnrollmentTracker: First app activity detected during FinalizingSetup (SkipUserStatusPage), switching to AppsUser phase");
                        lock (_stateLock) { _hasAutoSwitchedToAppsPhase = true; }
                        EmitImeTrackerEvent(new EnrollmentEvent
                        {
                            SessionId = _sessionId,
                            TenantId = _tenantId,
                            EventType = "esp_phase_changed",
                            Severity = EventSeverity.Info,
                            Source = "EnrollmentTracker",
                            Phase = EnrollmentPhase.AppsUser,
                            Message = "ESP phase: AppsUser (auto-detected from app activity)",
                            Data = new Dictionary<string, object> { { "espPhase", "AppsUser" }, { "autoDetected", true } },
                            ImmediateUpload = true
                        });
                    }
                }
            }

            // Only emit strategic events for significant state transitions
            string eventType;
            var severity = EventSeverity.Info;
            var phase = EnrollmentPhase.Unknown; // Apps set to Unknown, will be sorted chronologically into active phase

            switch (newState)
            {
                case AppInstallationState.Downloading:
                    // Emit strategic event once when download starts
                    if (oldState < AppInstallationState.Downloading)
                    {
                        eventType = "app_download_started";
                    }
                    else
                    {
                        // Emit debug event for download progress updates
                        // Skip if no real download data (bytesTotal too small or zero)
                        if (app.BytesTotal > 1024) // At least 1 KB to be a real download
                        {
                            EmitImeTrackerEvent(new EnrollmentEvent
                            {
                                SessionId = _sessionId,
                                TenantId = _tenantId,
                                EventType = "download_progress",
                                Severity = EventSeverity.Debug,
                                Source = "ImeLogTracker",
                                Phase = phase,
                                Message = $"{app.Name ?? app.Id}: {app.ProgressPercent}%",
                                Data = app.ToEventData()
                            });
                        }
                        return; // Skip main event emission below
                    }
                    break;

                case AppInstallationState.Installing:
                    // Only emit the strategic event once when install actually starts.
                    // Progress-only updates (oldState == Installing, just progress/bytes changed)
                    // are skipped — same pattern as Downloading above.
                    if (oldState == AppInstallationState.Installing)
                        return;
                    eventType = "app_install_started";
                    break;

                case AppInstallationState.Installed:
                    eventType = "app_install_completed";
                    // Emit download_progress event for download manager (shows as completed)
                    EmitImeTrackerEvent(new EnrollmentEvent
                    {
                        SessionId = _sessionId,
                        TenantId = _tenantId,
                        EventType = "download_progress",
                        Severity = EventSeverity.Debug,
                        Source = "ImeLogTracker",
                        Phase = phase,
                        Message = $"{app.Name ?? app.Id}: completed",
                        Data = new Dictionary<string, object>(app.ToEventData())
                        {
                            ["status"] = "completed"
                        }
                    });
                    break;

                case AppInstallationState.Skipped:
                    eventType = "app_install_skipped";
                    break;

                case AppInstallationState.Error:
                    eventType = "app_install_failed";
                    severity = EventSeverity.Error;
                    // Emit download_progress event for download manager (shows as failed)
                    EmitImeTrackerEvent(new EnrollmentEvent
                    {
                        SessionId = _sessionId,
                        TenantId = _tenantId,
                        EventType = "download_progress",
                        Severity = EventSeverity.Debug,
                        Source = "ImeLogTracker",
                        Phase = phase,
                        Message = $"{app.Name ?? app.Id}: failed",
                        Data = new Dictionary<string, object>(app.ToEventData())
                        {
                            ["status"] = "failed"
                        }
                    });
                    break;

                case AppInstallationState.Postponed:
                    eventType = "app_install_postponed";
                    severity = EventSeverity.Warning;
                    break;

                default:
                    return; // Don't emit for Unknown, NotInstalled, InProgress
            }

            // Build a descriptive message: include error detail if available
            var message = $"{app.Name ?? app.Id}: {newState}";
            if (newState == AppInstallationState.Error && !string.IsNullOrEmpty(app.ErrorDetail))
                message = $"{app.Name ?? app.Id}: {app.ErrorDetail}";

            EmitImeTrackerEvent(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = eventType,
                Severity = severity,
                Source = "ImeLogTracker",
                Phase = phase,
                Message = message,
                Data = app.ToEventData(),
                ImmediateUpload = true
            });

            // Emit summary immediately if state-breakdown counters changed (instant UI updates)
            EmitAppTrackingSummaryIfChanged();
        }

        private void HandlePoliciesDiscovered(string policiesJson)
        {
            _logger.Info($"EnrollmentTracker: policies discovered, tracking {_imeLogTracker.PackageStates.Count} apps");
        }

        private void HandleAllAppsCompleted()
        {
            _logger.Info("EnrollmentTracker: all apps completed");

            // Stop timers
            _summaryTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _summaryTimerActive = false;
            _debugStateTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _debugStateTimerActive = false;

            // Emit final summary
            EmitAppTrackingSummary();

            // Note: Phase transition to FinalizingSetup is now handled by Shell-Core events
            // (ESP exit or Hello wizard start) for more robust detection.
            // We no longer automatically transition here when apps complete.
            string phaseSnap;
            lock (_stateLock) { phaseSnap = _lastEspPhase; }
            if (phaseSnap != null)
            {
                _logger.Info($"EnrollmentTracker: All apps completed while in phase '{phaseSnap}'");
                _logger.Info("EnrollmentTracker: Waiting for ESP exit or Hello wizard events to transition to FinalizingSetup");
            }
        }

        private void HandleDoTelemetryReceived(AppPackageState app)
        {
            _logger.Info($"EnrollmentTracker: DO telemetry received for {app.Name ?? app.Id}");

            var phase = EnrollmentPhase.Unknown;

            // Dedicated do_telemetry event for backend aggregation
            EmitImeTrackerEvent(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = "do_telemetry",
                Severity = EventSeverity.Info,
                Source = "ImeLogTracker",
                Phase = phase,
                Message = $"{app.Name ?? app.Id}: DO complete - {app.DoPercentPeerCaching}% peers, mode={app.DoDownloadMode}",
                Data = app.ToEventData(),
                ImmediateUpload = true
            });

            // Also emit download_progress so the UI picks up DO stats
            EmitImeTrackerEvent(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = "download_progress",
                Severity = EventSeverity.Debug,
                Source = "ImeLogTracker",
                Phase = phase,
                Message = $"{app.Name ?? app.Id}: DO telemetry received",
                Data = app.ToEventData(),
                ImmediateUpload = true
            });
        }

        private void HandleScriptCompleted(ScriptExecutionState script)
        {
            if (script == null || string.IsNullOrEmpty(script.PolicyId))
            {
                _logger.Debug($"EnrollmentTracker: HandleScriptCompleted — skipped (script={script != null}, policyId='{script?.PolicyId}')");
                return;
            }

            var isSuccess = string.Equals(script.Result, "Success", StringComparison.OrdinalIgnoreCase)
                            || (script.ExitCode.HasValue && script.ExitCode.Value == 0 && string.IsNullOrEmpty(script.Result));

            var eventType = isSuccess
                ? Constants.EventTypes.ScriptCompleted
                : Constants.EventTypes.ScriptFailed;

            var severity = isSuccess ? EventSeverity.Info : EventSeverity.Warning;

            // Build human-readable message
            string shortId = script.PolicyId.Length >= 8 ? script.PolicyId.Substring(0, 8) : script.PolicyId;
            string message;
            if (string.Equals(script.ScriptType, "remediation", StringComparison.OrdinalIgnoreCase))
            {
                var complianceStr = string.Equals(script.ComplianceResult, "True", StringComparison.OrdinalIgnoreCase)
                    ? "Compliant" : "Non-compliant";
                message = $"Remediation {script.ScriptPart ?? "detection"} {shortId}: {complianceStr} (exit: {script.ExitCode ?? -1})";
            }
            else
            {
                message = $"Platform script {shortId}: {script.Result ?? "Unknown"} (exit: {script.ExitCode ?? -1})";
            }

            // Add stderr hint to message if present
            if (!string.IsNullOrEmpty(script.Stderr) && script.Stderr.Trim().Length > 0)
                message += " - stderr present";

            var data = new Dictionary<string, object>
            {
                ["policyId"] = script.PolicyId,
                ["scriptType"] = script.ScriptType ?? "platform",
                ["exitCode"] = script.ExitCode ?? -1
            };

            if (!string.IsNullOrEmpty(script.RunContext))
                data["runContext"] = script.RunContext;
            if (!string.IsNullOrEmpty(script.Result))
                data["result"] = script.Result;
            if (!string.IsNullOrEmpty(script.Stdout) && script.Stdout.Trim().Length > 0)
                data["stdout"] = script.Stdout;
            if (!string.IsNullOrEmpty(script.Stderr) && script.Stderr.Trim().Length > 0)
                data["stderr"] = script.Stderr;
            if (!string.IsNullOrEmpty(script.ComplianceResult))
                data["complianceResult"] = script.ComplianceResult;
            if (!string.IsNullOrEmpty(script.ScriptPart))
                data["scriptPart"] = script.ScriptPart;

            EmitImeTrackerEvent(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = eventType,
                Severity = severity,
                Source = Constants.EventSources.IME,
                Phase = EnrollmentPhase.Unknown,
                Message = message,
                Data = data
            });

            _logger.Info($"EnrollmentTracker: {message}");
        }

        private void HandleImeSessionChange(string changeType)
        {
            EmitImeTrackerEvent(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = "ime_session_change",
                Severity = EventSeverity.Debug,
                Source = "ImeLogTracker",
                Phase = EnrollmentPhase.Unknown,
                Message = $"IME session change: {changeType}",
                Data = new Dictionary<string, object> { { "changeType", changeType ?? "" } }
            });
        }

        private void HandleUserSessionCompleted()
        {
            _logger.Info("EnrollmentTracker: User session completed (detected from IME log)");
            _stateData.ImePatternSeenUtc = DateTime.UtcNow;
            RecordSignal("ime_pattern");

            // Emit timeline event so admins can see exactly when IME finished its user part
            EmitImeTrackerEvent(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = "ime_user_session_completed",
                Severity = EventSeverity.Info,
                Source = "ImeLogTracker",
                Phase = EnrollmentPhase.Unknown,
                Message = "IME user session completed",
                Data = new Dictionary<string, object> { { "detectedAt", DateTime.UtcNow.ToString("o") } }
            });

            // User session completed successfully — cancel any pending ESP failure
            CancelPendingEspFailure();

            // Stop timers if running
            _summaryTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _summaryTimerActive = false;
            _debugStateTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _debugStateTimerActive = false;

            // Device-only deployment (Self-Deploying or SkipUserStatusPage=true): skip Hello wait
            // entirely — no interactive user session means Hello provisioning cannot complete.
            bool isDeviceOnlySnap;
            int? autopilotModeSnap;
            bool? skipUserSnap;
            lock (_stateLock)
            {
                isDeviceOnlySnap = IsDeviceOnlyDeployment;
                autopilotModeSnap = _autopilotMode;
                skipUserSnap = _skipUserStatusPage;
            }

            if (isDeviceOnlySnap)
            {
                _logger.Info($"EnrollmentTracker: Device-only deployment (autopilotMode={autopilotModeSnap}, skipUserStatusPage={skipUserSnap}) — skipping Hello wait, proceeding to completion");
                TryEmitEnrollmentComplete("ime_pattern");
                ShadowProcessTrigger("ime_user_session_completed");
                return;
            }

            // Check if Windows Hello is configured but not yet completed
            if (_espAndHelloTracker != null)
            {
                bool helloPolicyConfigured = _espAndHelloTracker.IsPolicyConfigured;
                bool helloCompleted = _espAndHelloTracker.IsHelloCompleted;

                if (helloPolicyConfigured && !helloCompleted)
                {
                    // Hello is configured but not finished yet - DO NOT mark enrollment as complete
                    _logger.Info("EnrollmentTracker: Windows Hello policy is configured but provisioning has not completed yet.");
                    _logger.Info("EnrollmentTracker: Waiting for Hello provisioning to finish before marking enrollment as complete.");

                    EmitImeTrackerEvent(new EnrollmentEvent
                    {
                        SessionId = _sessionId,
                        TenantId = _tenantId,
                        EventType = "waiting_for_hello",
                        Severity = EventSeverity.Info,
                        Source = "EnrollmentTracker",
                        Phase = EnrollmentPhase.FinalizingSetup,
                        Message = "User apps completed - waiting for Windows Hello provisioning to finish",
                        ImmediateUpload = true
                    });

                    // Set flag so we know we're waiting
                    lock (_stateLock)
                    {
                        _isWaitingForHello = true;
                        _stateData.IsWaitingForHello = true;
                        _stateData.WaitingForHelloStartedUtc = DateTime.UtcNow;
                        _stateDirty = true;
                    }
                    _statePersistence.Save(_stateData); // Immediate persist — summary timer was just stopped

                    // Defense-in-depth: Ensure Hello wait timer is running.
                    // StartHelloWaitTimer() has internal guards (returns if timer already running,
                    // if Hello already completed, or if wizard already started). Safe to call multiple times.
                    _espAndHelloTracker?.StartHelloWaitTimer();

                    // Safety net: if Hello never resolves (timer chain fails, agent crash without restart),
                    // force enrollment_complete after 7 minutes instead of waiting for the 6h max lifetime timer.
                    _waitingForHelloSafetyTimer?.Dispose();
                    _waitingForHelloSafetyTimer = new Timer(
                        OnWaitingForHelloSafetyTimeout,
                        null,
                        TimeSpan.FromSeconds(WaitingForHelloSafetyTimeoutSeconds),
                        TimeSpan.FromMilliseconds(-1));

                    ShadowProcessTrigger("ime_user_session_completed");
                    return;
                }
            }

            // ESP provisioning settle check: if ESP categories are still resolving in the registry,
            // wait up to 30s for Windows to finalize the outcomes before completing.
            // This catches cases where the agent started late and the ESP status hasn't settled yet.
            if (_espAndHelloTracker != null)
            {
                var snapshot = _espAndHelloTracker.GetProvisioningSnapshot();
                if (snapshot != null && snapshot.CategoriesSeen > 0 && !snapshot.AllResolved)
                {
                    var unresolvedCategories = snapshot.CategoryOutcomes
                        .Where(kvp => kvp.Value == "in_progress")
                        .Select(kvp => kvp.Key)
                        .ToList();

                    _logger.Info($"EnrollmentTracker: ESP provisioning categories not yet resolved " +
                        $"({string.Join(", ", unresolvedCategories)}) — waiting up to {EspSettleTimeoutSeconds}s for settlement");

                    EmitImeTrackerEvent(new EnrollmentEvent
                    {
                        SessionId = _sessionId,
                        TenantId = _tenantId,
                        EventType = "esp_provisioning_settle_started",
                        Severity = EventSeverity.Info,
                        Source = "EnrollmentTracker",
                        Phase = EnrollmentPhase.Unknown,
                        Message = $"ESP provisioning settle wait started — {unresolvedCategories.Count} category(ies) unresolved: {string.Join(", ", unresolvedCategories)}",
                        Data = new Dictionary<string, object>
                        {
                            { "unresolvedCategories", unresolvedCategories },
                            { "settleTimeoutSeconds", EspSettleTimeoutSeconds },
                            { "categoriesSeen", snapshot.CategoriesSeen },
                            { "categoriesResolved", snapshot.CategoriesResolved }
                        },
                        ImmediateUpload = true
                    });

                    lock (_stateLock)
                    {
                        _isWaitingForEspSettle = true;
                        _stateData.IsWaitingForEspSettle = true;
                        _stateData.WaitingForEspSettleStartedUtc = DateTime.UtcNow;
                        _stateDirty = true;
                    }
                    _statePersistence.Save(_stateData);

                    _waitingForEspSettleTimer?.Dispose();
                    _waitingForEspSettleTimer = new Timer(
                        OnEspSettleTimerExpired,
                        null,
                        TimeSpan.FromSeconds(EspSettleTimeoutSeconds),
                        TimeSpan.FromMilliseconds(-1));

                    ShadowProcessTrigger("ime_user_session_completed");
                    return;
                }
            }

            TryEmitEnrollmentComplete("ime_pattern");

            // Shadow state machine: track IME user session completed
            ShadowProcessTrigger("ime_user_session_completed");
        }

        /// <summary>
        /// Checks whether a real user profile exists in C:\Users\ (beyond system accounts).
        /// Used to verify AccountSetup phase: during WhiteGlove pre-provisioning only defaultuser0
        /// exists; in user-driven enrollment the signed-in user's profile is already created.
        /// </summary>
        private bool HasRealUserProfile()
        {
            try
            {
                var usersDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                // Navigate to the Users root (parent of the current profile)
                usersDir = Path.GetDirectoryName(usersDir); // e.g. C:\Users
                if (string.IsNullOrEmpty(usersDir) || !Directory.Exists(usersDir))
                    usersDir = @"C:\Users";

                var excludedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "defaultuser0", "defaultuser1", "Default", "Default User",
                    "Public", "All Users", "Administrator", "Guest"
                };

                foreach (var dir in Directory.GetDirectories(usersDir))
                {
                    var name = Path.GetFileName(dir);
                    if (excludedNames.Contains(name))
                        continue;
                    if (name.StartsWith("DefaultUser", StringComparison.OrdinalIgnoreCase))
                        continue;

                    _logger.Debug($"EnrollmentTracker: HasRealUserProfile found '{name}'");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTracker: HasRealUserProfile check failed: {ex.Message}");
                return false; // Fail safe: treat as no real user (suppress AccountSetup)
            }
        }
    }
}
