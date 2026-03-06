using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.Core.Monitoring.Tracking
{
    /// <summary>
    /// Partial: Enrollment completion detection, Hello/WhiteGlove/ESP failure handling,
    /// state persistence, and multi-signal completion logic.
    /// </summary>
    public partial class EnrollmentTracker
    {
        private void OnHelloCompleted(object sender, EventArgs e)
        {
            _logger.Info("EnrollmentTracker: Received HelloCompleted event from EspAndHelloTracker");
            _stateData.HelloResolvedUtc = DateTime.UtcNow;
            RecordSignal("hello_resolved");

            // Cancel safety timer — Hello resolved normally
            _waitingForHelloSafetyTimer?.Change(Timeout.Infinite, Timeout.Infinite);

            if (_isWaitingForHello)
            {
                _logger.Info("EnrollmentTracker: Hello provisioning completed while waiting (IME path) - marking enrollment as complete now");
                _isWaitingForHello = false;
                TryEmitEnrollmentComplete("ime_hello");
            }
            else if (_espFinalExitSeen)
            {
                _logger.Info("EnrollmentTracker: Hello completed + ESP final exit seen — composite completion");
                TryEmitEnrollmentComplete("esp_hello_composite");
            }
            else if (_desktopArrived)
            {
                _logger.Info("EnrollmentTracker: Hello completed + desktop arrived — desktop-hello completion");
                TryEmitEnrollmentComplete("desktop_hello");
            }
            else
            {
                _logger.Debug("EnrollmentTracker: HelloCompleted event received but no completion trigger active yet");
            }
        }

        /// <summary>
        /// Called when WhiteGlove (Pre-Provisioning) completes successfully.
        /// Emits the whiteglove_complete event. The MonitoringService handles
        /// the actual agent shutdown upon seeing this event type.
        /// </summary>
        private void OnWhiteGloveCompleted(object sender, EventArgs e)
        {
            _logger.Info("EnrollmentTracker: WhiteGlove pre-provisioning completed");

            // Stop summary timer — no more app tracking needed
            _summaryTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            _summaryTimerActive = false;

            _emitEvent(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = "whiteglove_complete",
                Severity = EventSeverity.Info,
                Source = "EnrollmentTracker",
                Phase = EnrollmentPhase.Complete,
                Message = "WhiteGlove (Pre-Provisioning) completed \u2014 device entering pending state"
            });

            // WICHTIG: Kein WriteEnrollmentCompleteMarker()!
            // Das Marker-File wuerde verhindern, dass der Agent beim naechsten Start wieder laeuft.
            // WICHTIG: Kein _imeLogTracker?.DeleteState()!
            // Der State wird fuer Part 2 benoetigt falls der Tracker weiterlaufen muss.
        }

        /// <summary>
        /// Called when an ESP failure is detected (ESPProgress_Failure, _Timeout, _Abort, WhiteGlove_Failed, etc.).
        /// Terminal failures emit enrollment_failed immediately.
        /// Recoverable failures (e.g. ESPProgress_Timeout) get a grace period before failure.
        /// </summary>
        private void OnEspFailureDetected(object sender, string failureType)
        {
            _logger.Info($"EnrollmentTracker: ESP failure detected: {failureType}");

            if (RecoverableEspFailureTypes.Contains(failureType))
            {
                // Recoverable failure — start grace period timer
                _logger.Info($"EnrollmentTracker: '{failureType}' is recoverable — starting {EspFailureGracePeriodSeconds}s grace period");
                _pendingEspFailureType = failureType;

                // Cancel existing timer if any (e.g. second timeout event)
                _espFailureTimer?.Dispose();
                _espFailureTimer = new Timer(
                    OnEspFailureGracePeriodExpired,
                    null,
                    TimeSpan.FromSeconds(EspFailureGracePeriodSeconds),
                    TimeSpan.FromMilliseconds(-1));
            }
            else
            {
                // Terminal failure — emit enrollment_failed immediately
                _logger.Info($"EnrollmentTracker: '{failureType}' is terminal — emitting enrollment_failed immediately");
                EmitEnrollmentFailed(failureType, "esp_failure");
            }
        }

        /// <summary>
        /// Called when the ESP failure grace period expires without recovery.
        /// Emits enrollment_failed.
        /// </summary>
        private void OnEspFailureGracePeriodExpired(object state)
        {
            var failureType = _pendingEspFailureType ?? "unknown";
            _logger.Warning($"EnrollmentTracker: ESP failure grace period ({EspFailureGracePeriodSeconds}s) expired for '{failureType}' — emitting enrollment_failed");
            _pendingEspFailureType = null;

            EmitEnrollmentFailed(failureType, "esp_failure_grace_expired");
        }

        /// <summary>
        /// Cancels any pending ESP failure grace period timer (called when recovery is detected).
        /// </summary>
        private void CancelPendingEspFailure()
        {
            if (_espFailureTimer != null)
            {
                _logger.Info($"EnrollmentTracker: ESP recovery detected — cancelling pending failure for '{_pendingEspFailureType}'");
                _espFailureTimer.Dispose();
                _espFailureTimer = null;
                _pendingEspFailureType = null;
            }
        }

        /// <summary>
        /// Safety-net callback: fires 7 minutes after waiting_for_hello was set.
        /// If the normal Hello timeout chain (30s + 300s) failed for any reason,
        /// this forces enrollment_complete instead of waiting for the 6h max lifetime timer.
        /// </summary>
        private void OnWaitingForHelloSafetyTimeout(object state)
        {
            if (!_isWaitingForHello || _enrollmentCompleteEmitted)
                return;

            _logger.Warning($"EnrollmentTracker: waiting_for_hello safety timeout ({WaitingForHelloSafetyTimeoutSeconds}s) expired — forcing completion");
            _isWaitingForHello = false;

            // Force-resolve Hello so TryEmitEnrollmentComplete's hello check passes
            if (_espAndHelloTracker != null && !_espAndHelloTracker.IsHelloCompleted)
            {
                _espAndHelloTracker.ForceMarkHelloCompleted("safety_timeout");
            }

            TryEmitEnrollmentComplete("ime_hello_safety_timeout");
        }

        /// <summary>
        /// Emits an enrollment_failed event. The MonitoringService handles shutdown identically to enrollment_complete.
        /// </summary>
        private void EmitEnrollmentFailed(string failureType, string failureSource)
        {
            // Stop timers
            _summaryTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _summaryTimerActive = false;
            _debugStateTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _debugStateTimerActive = false;

            _emitEvent(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = "enrollment_failed",
                Severity = EventSeverity.Error,
                Source = "EnrollmentTracker",
                Phase = EnrollmentPhase.Complete,
                Message = $"Autopilot enrollment failed: {failureType}",
                Data = new Dictionary<string, object>
                {
                    { "failureType", failureType },
                    { "failureSource", failureSource },
                    { "signalsSeen", _stateData.SignalsSeen },
                    { "agentUptimeSeconds", (DateTime.UtcNow - _agentStartTimeUtc).TotalSeconds }
                }
            });

            // Write final status dump before cleanup
            WriteFinalStatus("failed", failureType);

            // Clean up persisted tracker state
            _imeLogTracker?.DeleteState();
            _statePersistence.Delete();
        }

        /// <summary>
        /// Called when ESP exit or Hello wizard start is detected (via HelloDetector Shell-Core events)
        /// Triggers transition to FinalizingSetup phase
        /// </summary>
        private void OnFinalizingSetupPhaseTriggered(object sender, string reason)
        {
            _logger.Info($"EnrollmentTracker: FinalizingSetup phase trigger received - reason: {reason}");

            // If ESP exiting, check which phase we're in
            if (reason == "esp_exiting")
            {
                _espEverSeen = true;

                if (string.Equals(_lastEspPhase, "AccountSetup", StringComparison.OrdinalIgnoreCase) || _desktopArrived)
                {
                    // Final ESP exit: either AccountSetup phase detected OR desktop arrived (backup)
                    var phaseInfo = string.Equals(_lastEspPhase, "AccountSetup", StringComparison.OrdinalIgnoreCase)
                        ? "AccountSetup"
                        : $"{_lastEspPhase ?? "unknown"} (desktop arrival backup)";
                    _logger.Info($"EnrollmentTracker: ESP final exit from {phaseInfo} — marking _espFinalExitSeen, starting Hello wait timer");

                    _espFinalExitSeen = true;
                    _stateData.EspFinalExitSeen = true;
                    _stateData.EspFinalExitUtc = DateTime.UtcNow;
                    RecordSignal("esp_final_exit");

                    // Emit phase change event to FinalizingSetup
                    _emitEvent(new EnrollmentEvent
                    {
                        SessionId = _sessionId,
                        TenantId = _tenantId,
                        EventType = "esp_phase_changed",
                        Severity = EventSeverity.Info,
                        Source = "EnrollmentTracker",
                        Phase = EnrollmentPhase.FinalizingSetup,
                        Message = $"ESP phase: FinalizingSetup ({phaseInfo} completed, waiting for final steps)",
                        Data = new Dictionary<string, object>
                        {
                            { "espPhase", "FinalizingSetup" },
                            { "autoDetected", true },
                            { "triggeredBy", reason },
                            { "previousPhase", _lastEspPhase ?? "unknown" },
                            { "desktopArrivedBackup", _desktopArrived && !string.Equals(_lastEspPhase, "AccountSetup", StringComparison.OrdinalIgnoreCase) }
                        }
                    });

                    CollectDeviceInfoAtFinalizingSetup(reason);

                    // Start Hello wait timer (waits for Hello wizard to start or timeout)
                    _espAndHelloTracker?.StartHelloWaitTimer();

                    // If Hello was already resolved (e.g., via EventLog backfill or Event 300/301
                    // during AccountSetup), the composite signal can fire immediately.
                    TryEmitEnrollmentComplete("esp_hello_composite");
                }
                else if (_skipUserStatusPage == true)
                {
                    // Registry definitively says no AccountSetup expected → immediate device-only classification
                    _logger.Info($"EnrollmentTracker: ESP phase exiting from '{_lastEspPhase ?? "unknown"}' — SkipUserStatusPage=true, classified as device-only ESP (registry)");

                    _espFinalExitSeen = true;
                    _stateData.EspFinalExitSeen = true;
                    _stateData.EspFinalExitUtc = DateTime.UtcNow;
                    RecordSignal("device_only_esp_registry");

                    _emitEvent(new EnrollmentEvent
                    {
                        SessionId = _sessionId,
                        TenantId = _tenantId,
                        EventType = "esp_phase_changed",
                        Severity = EventSeverity.Info,
                        Source = "EnrollmentTracker",
                        Phase = EnrollmentPhase.FinalizingSetup,
                        Message = "ESP phase: FinalizingSetup (device-only ESP — SkipUserStatusPage confirmed via registry)",
                        Data = new Dictionary<string, object>
                        {
                            { "espPhase", "FinalizingSetup" },
                            { "autoDetected", true },
                            { "triggeredBy", "device_only_esp_registry" },
                            { "previousPhase", _lastEspPhase ?? "unknown" },
                            { "skipUserStatusPage", true }
                        }
                    });

                    CollectDeviceInfoAtFinalizingSetup("device_only_esp_registry");
                    _espAndHelloTracker?.StartHelloWaitTimer();
                    TryEmitEnrollmentComplete("device_only_esp_registry");
                }
                else
                {
                    // Registry keys unknown or SkipUserStatusPage=false → fallback to timer-based detection
                    var fallbackReason = _skipUserStatusPage == null ? "registry keys not found" : "SkipUserStatusPage=false";
                    _logger.Info($"EnrollmentTracker: ESP phase exiting from '{_lastEspPhase ?? "unknown"}' — {fallbackReason}, starting device-only ESP detection timer ({DeviceOnlyEspTimerMinutes}min)");

                    // Start device-only ESP detection timer
                    _deviceOnlyEspTimer?.Dispose();
                    _deviceOnlyEspTimer = new Timer(
                        OnDeviceOnlyEspTimerExpired,
                        null,
                        TimeSpan.FromMinutes(DeviceOnlyEspTimerMinutes),
                        TimeSpan.FromMilliseconds(-1));
                }
            }
            else if (reason == "hello_wizard_started")
            {
                // Hello wizard started - transition to FinalizingSetup regardless of previous phase
                _logger.Info("EnrollmentTracker: Hello wizard started - transitioning to FinalizingSetup phase");

                _emitEvent(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId = _tenantId,
                    EventType = "esp_phase_changed",
                    Severity = EventSeverity.Info,
                    Source = "EnrollmentTracker",
                    Phase = EnrollmentPhase.FinalizingSetup,
                    Message = "ESP phase: FinalizingSetup (Hello wizard started)",
                    Data = new Dictionary<string, object>
                    {
                        { "espPhase", "FinalizingSetup" },
                        { "autoDetected", true },
                        { "triggeredBy", reason }
                    }
                });

                CollectDeviceInfoAtFinalizingSetup(reason);
            }
        }

        // ===== State Persistence =====

        private void LoadState()
        {
            var loaded = _statePersistence.Load();
            if (loaded == null)
                return;

            _espEverSeen = loaded.EspEverSeen;
            _espFinalExitSeen = loaded.EspFinalExitSeen;
            _desktopArrived = loaded.DesktopArrived;
            _lastEspPhase = loaded.LastEspPhase;
            _isWaitingForHello = loaded.IsWaitingForHello;
            _enrollmentCompleteEmitted = loaded.EnrollmentCompleteEmitted;
            _enrollmentType = loaded.EnrollmentType ?? _enrollmentType;
            _skipUserStatusPage = loaded.SkipUserStatusPage;
            _skipDeviceStatusPage = loaded.SkipDeviceStatusPage;
            _stateData = loaded;

            _logger.Info($"EnrollmentTracker: state restored — espEverSeen={_espEverSeen}, espFinalExitSeen={_espFinalExitSeen}, desktopArrived={_desktopArrived}, lastEspPhase={_lastEspPhase}, enrollmentCompleteEmitted={_enrollmentCompleteEmitted}");

            // Restart Hello wait timer if needed after crash recovery
            if ((_desktopArrived || _espFinalExitSeen) && _espAndHelloTracker != null && !_espAndHelloTracker.IsHelloCompleted)
            {
                _logger.Info("EnrollmentTracker: restarting Hello wait timer after state recovery");
                _espAndHelloTracker.StartHelloWaitTimer();
            }

            // Restart safety timer if we were waiting for Hello
            if (_isWaitingForHello && !_enrollmentCompleteEmitted)
            {
                _logger.Info("EnrollmentTracker: restarting waiting_for_hello safety timer after state recovery");
                _waitingForHelloSafetyTimer?.Dispose();
                _waitingForHelloSafetyTimer = new Timer(
                    OnWaitingForHelloSafetyTimeout,
                    null,
                    TimeSpan.FromSeconds(WaitingForHelloSafetyTimeoutSeconds),
                    TimeSpan.FromMilliseconds(-1));
            }
        }

        private void RecordSignal(string signal)
        {
            if (!_stateData.SignalsSeen.Contains(signal))
                _stateData.SignalsSeen.Add(signal);
            _stateDirty = true;
        }

        // ===== Unified Completion Logic =====

        /// <summary>
        /// Central guard method for enrollment_complete emission. All completion paths route through here.
        /// An _enrollmentCompleteEmitted flag prevents double emission.
        /// Emits a throttled completion_check event on every call for observability.
        /// </summary>
        private void TryEmitEnrollmentComplete(string source)
        {
            if (_enrollmentCompleteEmitted)
            {
                _logger.Debug($"EnrollmentTracker: TryEmitEnrollmentComplete('{source}') — already emitted, skipping");
                return;
            }

            if (string.IsNullOrEmpty(source))
                return;

            // Hello-Check: Hello must be resolved before we can complete
            bool helloResolved = _espAndHelloTracker == null
                || _espAndHelloTracker.IsHelloCompleted
                || !_espAndHelloTracker.IsPolicyConfigured;

            if (!helloResolved)
            {
                _logger.Info($"EnrollmentTracker: Completion source '{source}' ready but Hello still pending — not completing yet");
                EmitCompletionCheck(source, "hello_pending", "hello_not_resolved");
                return;
            }

            // Desktop-Arrival Gate: Block desktop-based completion when ESP is still actively running.
            // Both "desktop_arrival" (direct) and "desktop_hello" (Hello resolved after desktop arrival)
            // must be gated — otherwise Hello timeout during active AccountSetup triggers premature completion.
            // WDP v2 has no ESP — skip the gate entirely.
            if ((source == "desktop_arrival" || source == "desktop_hello") && _enrollmentType != "v2" && _espEverSeen && !_espFinalExitSeen)
            {
                _logger.Info($"EnrollmentTracker: Completion source '{source}' blocked — ESP still active");
                EmitCompletionCheck(source, "blocked", "esp_active");
                return;
            }

            _enrollmentCompleteEmitted = true;
            _stateData.EnrollmentCompleteEmitted = true;

            // Stop timers
            _summaryTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _summaryTimerActive = false;
            _debugStateTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _debugStateTimerActive = false;

            var helloOutcome = _espAndHelloTracker?.HelloOutcome ?? "unknown";

            var signalTimestamps = new Dictionary<string, string>();
            if (_stateData.EspFirstSeenUtc.HasValue)
                signalTimestamps["espFirstSeen"] = _stateData.EspFirstSeenUtc.Value.ToString("o");
            if (_stateData.DesktopArrivedUtc.HasValue)
                signalTimestamps["desktopArrived"] = _stateData.DesktopArrivedUtc.Value.ToString("o");
            if (_stateData.EspFinalExitUtc.HasValue)
                signalTimestamps["espFinalExit"] = _stateData.EspFinalExitUtc.Value.ToString("o");
            if (_stateData.HelloResolvedUtc.HasValue)
                signalTimestamps["helloResolved"] = _stateData.HelloResolvedUtc.Value.ToString("o");
            if (_stateData.ImePatternSeenUtc.HasValue)
                signalTimestamps["imePatternSeen"] = _stateData.ImePatternSeenUtc.Value.ToString("o");

            _emitEvent(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = "enrollment_complete",
                Severity = EventSeverity.Info,
                Source = "EnrollmentTracker",
                Phase = EnrollmentPhase.Complete,
                Message = $"Autopilot enrollment completed successfully (source: {source})",
                Data = new Dictionary<string, object>
                {
                    { "completionSource", source },
                    { "helloOutcome", helloOutcome },
                    { "signalsSeen", _stateData.SignalsSeen },
                    { "signalTimestamps", signalTimestamps },
                    { "agentUptimeSeconds", (DateTime.UtcNow - _agentStartTimeUtc).TotalSeconds }
                }
            });

            // Write final status dump and enrollment complete marker
            WriteFinalStatus("completed", source);
            WriteEnrollmentCompleteMarker();

            // Clean up persisted tracker state so next enrollment starts fresh
            _imeLogTracker?.DeleteState();
            _statePersistence.Delete();
        }

        /// <summary>
        /// Emits a throttled completion_check event for observability.
        /// Max 1 event per minute per source to avoid flooding.
        /// </summary>
        private void EmitCompletionCheck(string source, string result, string reason)
        {
            // Throttle: max 1x per minute per source
            var now = DateTime.UtcNow;
            if (_lastCompletionCheckBySource.TryGetValue(source, out var lastEmit) && (now - lastEmit).TotalSeconds < 60)
                return;
            _lastCompletionCheckBySource[source] = now;

            _emitEvent(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = "completion_check",
                Severity = EventSeverity.Info,
                Source = "EnrollmentTracker",
                Phase = EnrollmentPhase.Unknown,
                Message = $"Completion source '{source}' evaluated — {result}",
                Data = new Dictionary<string, object>
                {
                    { "source", source },
                    { "result", result },
                    { "reason", reason },
                    { "espEverSeen", _espEverSeen },
                    { "espFinalExitSeen", _espFinalExitSeen },
                    { "desktopArrived", _desktopArrived },
                    { "helloResolved", _espAndHelloTracker?.IsHelloCompleted ?? false },
                    { "helloPolicyConfigured", _espAndHelloTracker?.IsPolicyConfigured ?? false },
                    { "enrollmentType", _enrollmentType },
                    { "lastEspPhase", _lastEspPhase ?? "none" },
                    { "skipUserStatusPage", _skipUserStatusPage?.ToString() ?? "unknown" },
                    { "skipDeviceStatusPage", _skipDeviceStatusPage?.ToString() ?? "unknown" }
                }
            });
        }

        /// <summary>
        /// Called by MonitoringService when Desktop Arrival is detected (explorer.exe under a real user).
        /// Corrects phase if needed, starts Hello wait timer in no-ESP scenarios, and attempts completion.
        /// </summary>
        public void NotifyDesktopArrived()
        {
            if (_desktopArrived)
                return;

            _desktopArrived = true;
            _stateData.DesktopArrived = true;
            _stateData.DesktopArrivedUtc = DateTime.UtcNow;
            RecordSignal("desktop_arrived");
            _logger.Info("EnrollmentTracker: Desktop arrival notified");

            // Phase correction: If ESP was seen but AccountSetup was never detected by IME log,
            // correct the phase and emit an event for the timeline
            if (_espEverSeen && !string.Equals(_lastEspPhase, "AccountSetup", StringComparison.OrdinalIgnoreCase))
            {
                var previousPhase = _lastEspPhase ?? "unknown";
                _lastEspPhase = "AccountSetup";
                _logger.Info($"EnrollmentTracker: Desktop arrival confirmed AccountSetup phase (was: {previousPhase}) — phase corrected on timeline");

                _emitEvent(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId = _tenantId,
                    EventType = "esp_phase_changed",
                    Severity = EventSeverity.Info,
                    Source = "DesktopArrivalDetector",
                    Phase = EnrollmentPhase.AccountSetup,
                    Message = $"ESP phase: AccountSetup (auto-detected from desktop arrival, was: {previousPhase})",
                    Data = new Dictionary<string, object>
                    {
                        { "espPhase", "AccountSetup" },
                        { "autoDetected", true },
                        { "correctedBy", "desktop_arrival" },
                        { "previousPhase", previousPhase }
                    }
                });
            }

            // Start Hello wait timer ONLY when ESP is NOT actively running.
            // During active ESP (AccountSetup runs in background with desktop visible),
            // the Hello timer must wait until ESP exits (started in OnFinalizingSetupPhaseTriggered).
            // Without this guard, Hello timeout-resolves while ESP still installs apps → premature completion.
            if (_espAndHelloTracker != null && !_espAndHelloTracker.IsHelloCompleted
                && (!_espEverSeen || _espFinalExitSeen))
            {
                _logger.Info("EnrollmentTracker: Desktop arrived with Hello pending (no active ESP) — starting Hello wait timer");
                _espAndHelloTracker.StartHelloWaitTimer();
            }

            TryEmitEnrollmentComplete("desktop_arrival");
        }

        /// <summary>
        /// Called when the device-only ESP timer expires. If no AccountSetup phase started
        /// and desktop is available, classify as device-only ESP and mark final exit.
        /// </summary>
        private void OnDeviceOnlyEspTimerExpired(object state)
        {
            // AccountSetup started meanwhile? Timer is obsolete
            if (string.Equals(_lastEspPhase, "AccountSetup", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Debug("EnrollmentTracker: Device-only ESP timer expired but AccountSetup detected — ignoring");
                return;
            }

            if (_desktopArrived)
            {
                _logger.Info($"EnrollmentTracker: No AccountSetup phase after {DeviceOnlyEspTimerMinutes}min — classified as device-only ESP, desktop is active");
                _espFinalExitSeen = true;
                _stateData.EspFinalExitSeen = true;
                _stateData.EspFinalExitUtc = DateTime.UtcNow;
                RecordSignal("device_only_esp_final_exit");
                _espAndHelloTracker?.StartHelloWaitTimer();
            }
            else
            {
                _logger.Info($"EnrollmentTracker: No AccountSetup phase after {DeviceOnlyEspTimerMinutes}min and no desktop yet — waiting for Desktop Arrival or Lifetime Timer");
            }
        }
    }
}
