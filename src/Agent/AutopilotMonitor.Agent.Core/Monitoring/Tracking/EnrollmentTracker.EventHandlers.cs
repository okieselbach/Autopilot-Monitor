using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.Core.Monitoring.Tracking
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
            // WDP (v2) has no ESP - skip ESP phase handling entirely
            if (_enrollmentType == "v2")
            {
                _logger.Debug($"EnrollmentTracker: skipping ESP phase event in WDP enrollment (phase: {phase})");
                return;
            }

            // Only emit event if the phase has actually changed
            if (string.Equals(phase, _lastEspPhase, StringComparison.OrdinalIgnoreCase))
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
                        new Dictionary<string, object> { { "espPhase", phase }, { "previousPhase", _lastEspPhase ?? "null" } });
                    return;
                }
            }

            _logger.Info($"EnrollmentTracker: ESP phase changed from '{_lastEspPhase ?? "null"}' to '{phase}'");
            _lastEspPhase = phase;
            _hasAutoSwitchedToAppsPhase = false; // Reset when ESP phase changes
            _espEverSeen = true;
            _stateData.EspEverSeen = true;
            _stateData.LastEspPhase = phase;
            if (_stateData.EspFirstSeenUtc == null)
                _stateData.EspFirstSeenUtc = DateTime.UtcNow;
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
                Data = new Dictionary<string, object> { { "espPhase", phase } }
            });

            // Re-collect enrollment-dependent device info (AAD join, profile, ESP config)
            // that may have been empty at startup (bootstrap / pre-enrollment scenario).
            CollectDeviceInfoAtEnrollmentStart();

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
            if (!_hasAutoSwitchedToAppsPhase &&
                (newState == AppInstallationState.Downloading || newState == AppInstallationState.Installing) &&
                oldState < AppInstallationState.Downloading)
            {
                if (_lastEspPhase != null)
                {
                    if (string.Equals(_lastEspPhase, "DeviceSetup", StringComparison.OrdinalIgnoreCase))
                    {
                        // Switch from DeviceSetup to AppsDevice
                        _logger.Info($"EnrollmentTracker: First app activity detected during DeviceSetup, switching to AppsDevice phase");
                        _hasAutoSwitchedToAppsPhase = true;
                        EmitImeTrackerEvent(new EnrollmentEvent
                        {
                            SessionId = _sessionId,
                            TenantId = _tenantId,
                            EventType = "esp_phase_changed",
                            Severity = EventSeverity.Info,
                            Source = "EnrollmentTracker",
                            Phase = EnrollmentPhase.AppsDevice,
                            Message = "ESP phase: AppsDevice (auto-detected from app activity)",
                            Data = new Dictionary<string, object> { { "espPhase", "AppsDevice" }, { "autoDetected", true } }
                        });
                    }
                    else if (string.Equals(_lastEspPhase, "AccountSetup", StringComparison.OrdinalIgnoreCase))
                    {
                        // Switch from AccountSetup to AppsUser
                        _logger.Info($"EnrollmentTracker: First app activity detected during AccountSetup, switching to AppsUser phase");
                        _hasAutoSwitchedToAppsPhase = true;
                        EmitImeTrackerEvent(new EnrollmentEvent
                        {
                            SessionId = _sessionId,
                            TenantId = _tenantId,
                            EventType = "esp_phase_changed",
                            Severity = EventSeverity.Info,
                            Source = "EnrollmentTracker",
                            Phase = EnrollmentPhase.AppsUser,
                            Message = "ESP phase: AppsUser (auto-detected from app activity)",
                            Data = new Dictionary<string, object> { { "espPhase", "AppsUser" }, { "autoDetected", true } }
                        });
                    }
                    else if (string.Equals(_lastEspPhase, "FinalizingSetup", StringComparison.OrdinalIgnoreCase))
                    {
                        // SkipUserStatusPage flow: apps starting after FinalizingSetup are background user apps
                        _logger.Info($"EnrollmentTracker: First app activity detected during FinalizingSetup (SkipUserStatusPage), switching to AppsUser phase");
                        _hasAutoSwitchedToAppsPhase = true;
                        EmitImeTrackerEvent(new EnrollmentEvent
                        {
                            SessionId = _sessionId,
                            TenantId = _tenantId,
                            EventType = "esp_phase_changed",
                            Severity = EventSeverity.Info,
                            Source = "EnrollmentTracker",
                            Phase = EnrollmentPhase.AppsUser,
                            Message = "ESP phase: AppsUser (auto-detected from app activity)",
                            Data = new Dictionary<string, object> { { "espPhase", "AppsUser" }, { "autoDetected", true } }
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
                    eventType = "app_install_failed";
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
                Data = app.ToEventData()
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
            if (_lastEspPhase != null)
            {
                _logger.Info($"EnrollmentTracker: All apps completed while in phase '{_lastEspPhase}'");
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
                Data = app.ToEventData()
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
                Data = app.ToEventData()
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
            _emitEvent(new EnrollmentEvent
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
            if (IsDeviceOnlyDeployment)
            {
                _logger.Info($"EnrollmentTracker: Device-only deployment (autopilotMode={_autopilotMode}, skipUserStatusPage={_skipUserStatusPage}) — skipping Hello wait, proceeding to completion");
                TryEmitEnrollmentComplete("ime_pattern");
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
                        Message = "User apps completed - waiting for Windows Hello provisioning to finish"
                    });

                    // Set flag so we know we're waiting
                    _isWaitingForHello = true;
                    _stateData.IsWaitingForHello = true;
                    _stateDirty = true;
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

                    return;
                }
            }

            TryEmitEnrollmentComplete("ime_pattern");
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
