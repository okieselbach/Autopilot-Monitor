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

            lock (_stateLock)
            {
                _stateData.HelloResolvedUtc = DateTime.UtcNow;
            }
            RecordSignal("hello_resolved");

            // Cancel safety timer — Hello resolved normally
            _waitingForHelloSafetyTimer?.Change(Timeout.Infinite, Timeout.Infinite);

            // Snapshot state under lock for branch decision
            bool wasWaiting, espFinalExit, desktopArr;
            lock (_stateLock)
            {
                wasWaiting = _isWaitingForHello;
                espFinalExit = _espFinalExitSeen;
                desktopArr = _desktopArrived;
                if (wasWaiting)
                    _isWaitingForHello = false;
            }

            if (wasWaiting)
            {
                _logger.Info("EnrollmentTracker: Hello provisioning completed while waiting (IME path) - marking enrollment as complete now");
                TryEmitEnrollmentComplete("ime_hello");
            }
            else if (espFinalExit)
            {
                _logger.Info("EnrollmentTracker: Hello completed + ESP final exit seen — composite completion");
                TryEmitEnrollmentComplete("esp_hello_composite");
            }
            else if (desktopArr)
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
        /// Called when DeviceSetup provisioning status shows all subcategories succeeded.
        /// In Self-Deploying mode, this is the primary completion signal because no user session
        /// means no Hello, no desktop arrival, and possibly no Shell-Core ESP exit event.
        /// In non-Self-Deploying mode, this is informational only — normal paths handle completion.
        /// </summary>
        private void OnDeviceSetupProvisioningComplete(object sender, EventArgs e)
        {
            _logger.Info("EnrollmentTracker: DeviceSetup provisioning completed successfully");
            RecordSignal("device_setup_provisioning_complete");
            lock (_stateLock)
            {
                _stateData.DeviceSetupProvisioningCompleteUtc = DateTime.UtcNow;
                _stateDirty = true;
            }

            bool isDeviceOnly;
            int? autopilotModeSnap;
            bool? skipUserSnap;
            lock (_stateLock)
            {
                isDeviceOnly = IsDeviceOnlyDeployment;
                autopilotModeSnap = _autopilotMode;
                skipUserSnap = _skipUserStatusPage;
            }

            if (!isDeviceOnly)
            {
                if (!_deviceInfoCollected)
                {
                    _logger.Info("EnrollmentTracker: DeviceSetup provisioning complete but device info not yet collected — " +
                                 "deferring classification decision until CollectDeviceInfo completes");
                    lock (_stateLock) { _pendingCompletionSource = "device_setup_provisioning_complete"; }
                    EmitTraceEvent("device_setup_complete_deferred",
                        "DeviceSetup provisioning complete but device classification pending — will re-evaluate after CollectDeviceInfo",
                        new Dictionary<string, object> { { "autopilotMode", autopilotModeSnap }, { "skipUserStatusPage", skipUserSnap } });
                    return;
                }

                _logger.Info("EnrollmentTracker: DeviceSetup provisioning complete but not device-only deployment — " +
                             "using normal completion paths (ESP exit + Hello)");
                EmitTraceEvent("device_setup_complete_non_device_only",
                    "DeviceSetup provisioning complete in non-device-only mode — no action, normal paths apply",
                    new Dictionary<string, object> { { "autopilotMode", autopilotModeSnap }, { "skipUserStatusPage", skipUserSnap } });
                return;
            }

            // Device-only deployment: this is our primary completion signal.
            // No user session → no Hello, no desktop arrival, possibly no Shell-Core ESP exit.
            _logger.Info($"EnrollmentTracker: Device-only deployment (autopilotMode={_autopilotMode}, skipUserStatusPage={_skipUserStatusPage}) + DeviceSetup provisioning complete — " +
                         "transitioning to FinalizingSetup and attempting completion");

            // Mark ESP as seen and final exit (provisioning success implies ESP device phase completed)
            bool needsSignal = false;
            lock (_stateLock)
            {
                if (!_espEverSeen)
                {
                    _espEverSeen = true;
                    _stateData.EspEverSeen = true;
                }
                if (!_espFinalExitSeen)
                {
                    _espFinalExitSeen = true;
                    _stateData.EspFinalExitSeen = true;
                    _stateData.EspFinalExitUtc = DateTime.UtcNow;
                    needsSignal = true;
                }
            }
            if (needsSignal)
            {
                _statePersistence.Save(_stateData); // Immediate persist — EspFinalExitUtc critical for hybrid reboot gate
                RecordSignal("self_deploying_esp_final_exit");
            }

            // Emit FinalizingSetup phase event
            _emitEvent(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = "esp_phase_changed",
                Severity = EventSeverity.Info,
                Source = "EnrollmentTracker",
                Phase = EnrollmentPhase.FinalizingSetup,
                Message = "ESP phase: FinalizingSetup (Self-Deploying — DeviceSetup provisioning complete, no user session expected)",
                Data = new Dictionary<string, object>
                {
                    { "espPhase", "FinalizingSetup" },
                    { "autoDetected", true },
                    { "triggeredBy", "self_deploying_provisioning_complete" },
                    { "autopilotMode", _autopilotMode },
                    { "skipUserStatusPage", _skipUserStatusPage }
                },
                ImmediateUpload = true
            });

            try { CollectDeviceInfoAtFinalizingSetup("self_deploying_provisioning_complete"); }
            catch (Exception ex) { _logger.Warning($"EnrollmentTracker: final device info collection failed (self_deploying): {ex.Message}"); }

            // Attempt completion — Hello guard is bypassed for device-only deployments (IsDeviceOnlyDeployment)
            TryEmitEnrollmentComplete("self_deploying_provisioning_complete");
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
                Message = "WhiteGlove (Pre-Provisioning) completed \u2014 device entering pending state",
                ImmediateUpload = true
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

                string previousPending;
                lock (_stateLock)
                {
                    previousPending = _pendingEspFailureType;
                    _pendingEspFailureType = failureType;
                }

                // Cancel existing timer if any (e.g. second timeout event)
                if (_espFailureTimer != null)
                    _logger.Debug($"EnrollmentTracker: resetting existing grace period timer (previous pending: '{previousPending}')");
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
            string failureType;
            lock (_stateLock)
            {
                failureType = _pendingEspFailureType ?? "unknown";
                _pendingEspFailureType = null;
            }
            _logger.Warning($"EnrollmentTracker: ESP failure grace period ({EspFailureGracePeriodSeconds}s) expired for '{failureType}' — emitting enrollment_failed");

            EmitEnrollmentFailed(failureType, "esp_failure_grace_expired");
        }

        /// <summary>
        /// Cancels any pending ESP failure grace period timer (called when recovery is detected).
        /// </summary>
        private void CancelPendingEspFailure()
        {
            if (_espFailureTimer != null)
            {
                string pending;
                lock (_stateLock) { pending = _pendingEspFailureType; }
                _logger.Info($"EnrollmentTracker: ESP recovery detected — cancelling pending failure for '{pending}'");
                _espFailureTimer.Dispose();
                _espFailureTimer = null;
                lock (_stateLock) { _pendingEspFailureType = null; }
            }
        }

        /// <summary>
        /// Safety-net callback: fires 7 minutes after waiting_for_hello was set.
        /// If the normal Hello timeout chain (30s + 300s) failed for any reason,
        /// this forces enrollment_complete instead of waiting for the 6h max lifetime timer.
        /// </summary>
        private void OnWaitingForHelloSafetyTimeout(object state)
        {
            bool isWaiting, alreadyEmitted;
            lock (_stateLock)
            {
                isWaiting = _isWaitingForHello;
                alreadyEmitted = _enrollmentCompleteEmitted;
                if (isWaiting && !alreadyEmitted)
                    _isWaitingForHello = false;
            }

            if (!isWaiting || alreadyEmitted)
            {
                _logger.Debug($"EnrollmentTracker: Hello safety timeout fired but not applicable (waiting={isWaiting}, emitted={alreadyEmitted})");
                return;
            }

            _logger.Warning($"EnrollmentTracker: waiting_for_hello safety timeout ({WaitingForHelloSafetyTimeoutSeconds}s) expired — forcing completion");

            // Force-resolve Hello so TryEmitEnrollmentComplete's hello check passes
            if (_espAndHelloTracker != null && !_espAndHelloTracker.IsHelloCompleted)
            {
                _espAndHelloTracker.ForceMarkHelloCompleted("safety_timeout");
            }

            TryEmitEnrollmentComplete("ime_hello_safety_timeout");
        }

        /// <summary>
        /// Timer callback: fires after EspSettleTimeoutSeconds (30s) when waiting for ESP provisioning
        /// categories to resolve. Completes enrollment regardless of whether categories settled.
        /// The snapshot captured in TryEmitEnrollmentComplete will reflect the final state.
        /// </summary>
        private void OnEspSettleTimerExpired(object state)
        {
            bool isWaiting, alreadyEmitted;
            lock (_stateLock)
            {
                isWaiting = _isWaitingForEspSettle;
                alreadyEmitted = _enrollmentCompleteEmitted;
                if (isWaiting && !alreadyEmitted)
                    _isWaitingForEspSettle = false;
            }

            if (!isWaiting || alreadyEmitted)
            {
                _logger.Debug($"EnrollmentTracker: ESP settle timer expired but not applicable " +
                    $"(waiting={isWaiting}, emitted={alreadyEmitted})");
                return;
            }

            _logger.Info($"EnrollmentTracker: ESP settle timer expired ({EspSettleTimeoutSeconds}s) " +
                "— proceeding to completion with current ESP provisioning state");
            RecordSignal("esp_provisioning_settled");
            TryEmitEnrollmentComplete("ime_pattern");
        }

        /// <summary>
        /// Safety-net callback for device-only ESP (SkipUserStatusPage=true) completion.
        /// If the normal Hello timer chain (30s wait + 300s completion) failed for any reason
        /// (exception, timer disposal, race condition), this forces enrollment_complete instead
        /// of waiting for the 6h max lifetime timer.
        /// </summary>
        private void OnDeviceOnlyCompletionSafetyTimeout(object state)
        {
            bool alreadyEmitted;
            lock (_stateLock) { alreadyEmitted = _enrollmentCompleteEmitted; }

            if (alreadyEmitted)
            {
                _logger.Debug("EnrollmentTracker: device-only completion safety timeout fired but enrollment already completed — ignoring");
                return;
            }

            _logger.Warning($"EnrollmentTracker: device-only ESP completion safety timeout ({DeviceOnlyCompletionSafetyTimeoutSeconds}s) expired — forcing completion");

            // Force-resolve Hello if still pending so the Hello guard in TryEmitEnrollmentComplete passes
            if (_espAndHelloTracker != null && !_espAndHelloTracker.IsHelloCompleted)
            {
                _espAndHelloTracker.ForceMarkHelloCompleted("device_only_safety_timeout");
            }

            TryEmitEnrollmentComplete("device_only_esp_safety_timeout");

            bool emittedAfterAttempt;
            lock (_stateLock) { emittedAfterAttempt = _enrollmentCompleteEmitted; }
            if (!emittedAfterAttempt)
            {
                _logger.Error("EnrollmentTracker: device-only ESP safety timeout — TryEmitEnrollmentComplete still did not fire (unexpected guard block)");
            }
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
                    { "signalsSeen", SnapshotSignalsSeen() },
                    { "agentUptimeSeconds", (DateTime.UtcNow - _agentStartTimeUtc).TotalSeconds }
                },
                ImmediateUpload = true
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
                string lastPhase;
                bool desktopArr;
                bool? skipUser;
                lock (_stateLock)
                {
                    _espEverSeen = true;
                    lastPhase = _lastEspPhase;
                    desktopArr = _desktopArrived;
                    skipUser = _skipUserStatusPage;
                }

                if (string.Equals(lastPhase, "AccountSetup", StringComparison.OrdinalIgnoreCase) || desktopArr)
                {
                    // Final ESP exit: either AccountSetup phase detected OR desktop arrived (backup)
                    var phaseInfo = string.Equals(lastPhase, "AccountSetup", StringComparison.OrdinalIgnoreCase)
                        ? "AccountSetup"
                        : $"{lastPhase ?? "unknown"} (desktop arrival backup)";
                    _logger.Info($"EnrollmentTracker: ESP final exit from {phaseInfo} — marking _espFinalExitSeen, starting Hello wait timer");

                    lock (_stateLock)
                    {
                        _espFinalExitSeen = true;
                        _stateData.EspFinalExitSeen = true;
                        _stateData.EspFinalExitUtc = DateTime.UtcNow;
                    }
                    _statePersistence.Save(_stateData); // Immediate persist — EspFinalExitUtc critical for hybrid reboot gate
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
                            { "previousPhase", lastPhase ?? "unknown" },
                            { "desktopArrivedBackup", desktopArr && !string.Equals(lastPhase, "AccountSetup", StringComparison.OrdinalIgnoreCase) }
                        },
                        ImmediateUpload = true
                    });

                    try { CollectDeviceInfoAtFinalizingSetup(reason); }
                    catch (Exception ex) { _logger.Warning($"EnrollmentTracker: final device info collection failed (esp_final_exit): {ex.Message}"); }

                    // Start Hello wait timer (waits for Hello wizard to start or timeout)
                    _espAndHelloTracker?.StartHelloWaitTimer();

                    // If Hello was already resolved (e.g., via EventLog backfill or Event 300/301
                    // during AccountSetup), the composite signal can fire immediately.
                    TryEmitEnrollmentComplete("esp_hello_composite");
                }
                else if (skipUser == true)
                {
                    // Registry definitively says no AccountSetup expected → immediate device-only classification
                    _logger.Info($"EnrollmentTracker: ESP phase exiting from '{lastPhase ?? "unknown"}' — SkipUserStatusPage=true, classified as device-only ESP (registry)");

                    lock (_stateLock)
                    {
                        _espFinalExitSeen = true;
                        _stateData.EspFinalExitSeen = true;
                        _stateData.EspFinalExitUtc = DateTime.UtcNow;
                    }
                    _statePersistence.Save(_stateData); // Immediate persist — EspFinalExitUtc critical for hybrid reboot gate
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
                            { "previousPhase", lastPhase ?? "unknown" },
                            { "skipUserStatusPage", true }
                        },
                        ImmediateUpload = true
                    });

                    try { CollectDeviceInfoAtFinalizingSetup("device_only_esp_registry"); }
                    catch (Exception ex) { _logger.Warning($"EnrollmentTracker: final device info collection failed (device_only_esp_registry): {ex.Message}"); }

                    _espAndHelloTracker?.StartHelloWaitTimer();
                    TryEmitEnrollmentComplete("device_only_esp_registry");

                    // Safety net: if enrollment_complete didn't fire immediately (Hello pending),
                    // start a safety timer to force completion. Without this, a failed Hello timer
                    // chain would leave the session hanging until the 6h max lifetime timer.
                    bool emittedYet;
                    lock (_stateLock) { emittedYet = _enrollmentCompleteEmitted; }
                    if (!emittedYet)
                    {
                        _logger.Info($"EnrollmentTracker: device-only ESP completion pending — starting safety timer ({DeviceOnlyCompletionSafetyTimeoutSeconds}s)");
                        _deviceOnlyCompletionSafetyTimer?.Dispose();
                        _deviceOnlyCompletionSafetyTimer = new Timer(
                            OnDeviceOnlyCompletionSafetyTimeout,
                            null,
                            TimeSpan.FromSeconds(DeviceOnlyCompletionSafetyTimeoutSeconds),
                            TimeSpan.FromMilliseconds(-1));
                    }
                }
                else
                {
                    // Registry keys unknown or SkipUserStatusPage=false → fallback to timer-based detection
                    var fallbackReason = skipUser == null ? "registry keys not found" : "SkipUserStatusPage=false";
                    _logger.Info($"EnrollmentTracker: ESP phase exiting from '{lastPhase ?? "unknown"}' — {fallbackReason}, starting device-only ESP detection timer ({DeviceOnlyEspTimerMinutes}min)");

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
                    },
                    ImmediateUpload = true
                });

                try { CollectDeviceInfoAtFinalizingSetup(reason); }
                catch (Exception ex) { _logger.Warning($"EnrollmentTracker: final device info collection failed (hello_wizard_started): {ex.Message}"); }
            }
        }

        // ===== State Persistence =====

        private void LoadState()
        {
            var loaded = _statePersistence.Load();
            if (loaded == null)
            {
                _logger.Debug("EnrollmentTracker: no persisted state found (fresh enrollment)");
                return;
            }

            lock (_stateLock)
            {
                _espEverSeen = loaded.EspEverSeen;
                _espFinalExitSeen = loaded.EspFinalExitSeen;
                _desktopArrived = loaded.DesktopArrived;
                _lastEspPhase = loaded.LastEspPhase;
                _isWaitingForHello = loaded.IsWaitingForHello;
                _isWaitingForEspSettle = loaded.IsWaitingForEspSettle;
                _enrollmentCompleteEmitted = loaded.EnrollmentCompleteEmitted;
                _enrollmentType = loaded.EnrollmentType ?? _enrollmentType;
                _skipUserStatusPage = loaded.SkipUserStatusPage;
                _skipDeviceStatusPage = loaded.SkipDeviceStatusPage;
                _autopilotMode = loaded.AutopilotMode;
                _aadJoinedWithUser = loaded.AadJoinedWithUser;
                // Restore hybrid join flag from persisted state; re-detect as fallback
                _isHybridJoin = loaded.IsHybridJoin || DetectHybridJoinStatic();
                _stateData = loaded;
            }

            _logger.Info($"EnrollmentTracker: state restored — espEverSeen={_espEverSeen}, espFinalExitSeen={_espFinalExitSeen}, desktopArrived={_desktopArrived}, lastEspPhase={_lastEspPhase}, enrollmentCompleteEmitted={_enrollmentCompleteEmitted}");

            // Restart Hello wait timer if needed after crash recovery
            if ((_desktopArrived || _espFinalExitSeen) && _espAndHelloTracker != null && !_espAndHelloTracker.IsHelloCompleted)
            {
                _logger.Info("EnrollmentTracker: restarting Hello wait timer after state recovery");
                _espAndHelloTracker.StartHelloWaitTimer();
            }

            // Restart safety timer if we were waiting for Hello — use remaining time, not full duration
            if (_isWaitingForHello && !_enrollmentCompleteEmitted)
            {
                var remaining = WaitingForHelloSafetyTimeoutSeconds;
                if (_stateData.WaitingForHelloStartedUtc.HasValue)
                {
                    var elapsed = (DateTime.UtcNow - _stateData.WaitingForHelloStartedUtc.Value).TotalSeconds;
                    remaining = Math.Max(0, WaitingForHelloSafetyTimeoutSeconds - (int)elapsed);
                }

                if (remaining <= 0)
                {
                    _logger.Warning("EnrollmentTracker: waiting_for_hello safety timeout already expired during crash recovery — forcing completion now");
                    _isWaitingForHello = false;
                    _waitingForHelloSafetyTimer?.Dispose();
                    _waitingForHelloSafetyTimer = new Timer(
                        OnWaitingForHelloSafetyTimeout,
                        null,
                        TimeSpan.Zero,
                        TimeSpan.FromMilliseconds(-1));
                }
                else
                {
                    _logger.Info($"EnrollmentTracker: restarting waiting_for_hello safety timer after state recovery ({remaining}s remaining)");
                    _waitingForHelloSafetyTimer?.Dispose();
                    _waitingForHelloSafetyTimer = new Timer(
                        OnWaitingForHelloSafetyTimeout,
                        null,
                        TimeSpan.FromSeconds(remaining),
                        TimeSpan.FromMilliseconds(-1));
                }
            }

            // Restart ESP settle timer if we were waiting — use remaining time, not full duration
            if (_isWaitingForEspSettle && !_enrollmentCompleteEmitted)
            {
                var remaining = EspSettleTimeoutSeconds;
                if (_stateData.WaitingForEspSettleStartedUtc.HasValue)
                {
                    var elapsed = (DateTime.UtcNow - _stateData.WaitingForEspSettleStartedUtc.Value).TotalSeconds;
                    remaining = Math.Max(0, EspSettleTimeoutSeconds - (int)elapsed);
                }

                if (remaining <= 0)
                {
                    _logger.Warning("EnrollmentTracker: ESP settle timeout already expired during crash recovery — completing now");
                    _isWaitingForEspSettle = false;
                    _waitingForEspSettleTimer?.Dispose();
                    _waitingForEspSettleTimer = new Timer(
                        OnEspSettleTimerExpired,
                        null,
                        TimeSpan.Zero,
                        TimeSpan.FromMilliseconds(-1));
                }
                else
                {
                    _logger.Info($"EnrollmentTracker: restarting ESP settle timer after state recovery ({remaining}s remaining)");
                    _waitingForEspSettleTimer?.Dispose();
                    _waitingForEspSettleTimer = new Timer(
                        OnEspSettleTimerExpired,
                        null,
                        TimeSpan.FromSeconds(remaining),
                        TimeSpan.FromMilliseconds(-1));
                }
            }
        }

        private void RecordSignal(string signal)
        {
            bool added;
            int count;
            lock (_stateLock)
            {
                if (!_stateData.SignalsSeen.Contains(signal))
                {
                    _stateData.SignalsSeen.Add(signal);
                    added = true;
                    count = _stateData.SignalsSeen.Count;
                }
                else
                {
                    added = false;
                    count = _stateData.SignalsSeen.Count;
                }
                _stateDirty = true;
            }

            if (added)
                _logger.Verbose($"EnrollmentTracker: signal recorded: '{signal}' (total: {count})");
            else
                _logger.Debug($"EnrollmentTracker: signal '{signal}' already recorded — deduplicated");
        }

        /// <summary>
        /// Returns a thread-safe snapshot of _stateData.SignalsSeen for use in event data.
        /// </summary>
        private List<string> SnapshotSignalsSeen()
        {
            lock (_stateLock)
            {
                return new List<string>(_stateData.SignalsSeen);
            }
        }

        // ===== Unified Completion Logic =====

        /// <summary>
        /// Central guard method for enrollment_complete emission. All completion paths route through here.
        /// An _enrollmentCompleteEmitted flag prevents double emission.
        /// Emits a throttled completion_check event on every call for observability.
        /// </summary>
        private void TryEmitEnrollmentComplete(string source)
        {
            if (string.IsNullOrEmpty(source))
            {
                _logger.Debug("EnrollmentTracker: TryEmitEnrollmentComplete called with empty source — ignoring");
                return;
            }

            // Atomically check all guards and claim the completion flag under a single lock.
            // This prevents the critical double-emission race where two threads (e.g. OnHelloCompleted
            // and OnWaitingForHelloSafetyTimeout) both read _enrollmentCompleteEmitted=false simultaneously.
            bool helloResolved;
            bool espGateBlocked;
            bool hybridRebootGateBlocked;
            bool alreadyEmitted;
            string enrollmentType;
            bool espEverSeen, espFinalExitSeen, desktopArrived;
            bool isDeviceOnly, isSelfDeploying;
            int? autopilotMode;
            List<string> signalsSeen;

            lock (_stateLock)
            {
                if (_enrollmentCompleteEmitted)
                {
                    alreadyEmitted = true;
                    helloResolved = false;
                    espGateBlocked = false;
                    hybridRebootGateBlocked = false;
                    enrollmentType = _enrollmentType;
                    espEverSeen = _espEverSeen;
                    espFinalExitSeen = _espFinalExitSeen;
                    desktopArrived = _desktopArrived;
                    isDeviceOnly = IsDeviceOnlyDeployment;
                    isSelfDeploying = IsSelfDeploying;
                    autopilotMode = _autopilotMode;
                    signalsSeen = null;
                }
                else
                {
                    alreadyEmitted = false;
                    enrollmentType = _enrollmentType;
                    espEverSeen = _espEverSeen;
                    espFinalExitSeen = _espFinalExitSeen;
                    desktopArrived = _desktopArrived;
                    isDeviceOnly = IsDeviceOnlyDeployment;
                    isSelfDeploying = IsSelfDeploying;
                    autopilotMode = _autopilotMode;

                    helloResolved = _espAndHelloTracker == null
                        || _espAndHelloTracker.IsHelloCompleted
                        || !_espAndHelloTracker.IsPolicyConfigured
                        || isDeviceOnly;

                    espGateBlocked = (source == "desktop_arrival" || source == "desktop_hello")
                        && enrollmentType != "v2" && espEverSeen && !espFinalExitSeen;

                    // GUARD 3: Hybrid Join reboot gate
                    // In hybrid join, ESP exit may be for a mid-enrollment reboot (domain user
                    // login required). Block esp_hello_composite unless we have stronger confirmation:
                    // - IME user session completed (strongest signal: IME says enrollment is done)
                    // - Agent restarted after ESP exit (reboot happened, we survived it)
                    hybridRebootGateBlocked = false;
                    if (_isHybridJoin && source == "esp_hello_composite")
                    {
                        bool hasImeCompletion = _stateData.ImePatternSeenUtc.HasValue;
                        bool agentRestartedAfterEspExit = _stateData.EspFinalExitUtc.HasValue
                            && _agentStartTimeUtc > _stateData.EspFinalExitUtc.Value;

                        hybridRebootGateBlocked = !hasImeCompletion && !agentRestartedAfterEspExit;
                    }

                    // If all guards pass, claim the flag atomically
                    if (helloResolved && !espGateBlocked && !hybridRebootGateBlocked)
                    {
                        _enrollmentCompleteEmitted = true;
                        _stateData.EnrollmentCompleteEmitted = true;
                        signalsSeen = new List<string>(_stateData.SignalsSeen);
                    }
                    else
                    {
                        signalsSeen = null;
                    }
                }
            }

            // All logging and event emission happens OUTSIDE the lock
            if (alreadyEmitted)
            {
                _logger.Debug($"EnrollmentTracker: TryEmitEnrollmentComplete('{source}') — already emitted, skipping");
                return;
            }

            _logger.Debug($"EnrollmentTracker: TryEmitEnrollmentComplete('{source}') — evaluating guards " +
                          $"[espEverSeen={espEverSeen}, espFinalExitSeen={espFinalExitSeen}, desktopArrived={desktopArrived}, " +
                          $"helloCompleted={_espAndHelloTracker?.IsHelloCompleted ?? false}, helloPolicyConfigured={_espAndHelloTracker?.IsPolicyConfigured ?? false}, " +
                          $"enrollmentType={enrollmentType}, autopilotMode={autopilotMode}, isSelfDeploying={isSelfDeploying}, isDeviceOnlyDeployment={isDeviceOnly}, " +
                          $"isHybridJoin={_isHybridJoin}]");

            if (!helloResolved)
            {
                _logger.Info($"EnrollmentTracker: Completion source '{source}' ready but Hello still pending — not completing yet");
                EmitCompletionCheck(source, "hello_pending", "hello_not_resolved");
                return;
            }

            if (espGateBlocked)
            {
                _logger.Info($"EnrollmentTracker: Completion source '{source}' blocked — ESP still active");
                EmitCompletionCheck(source, "blocked", "esp_active");
                return;
            }

            if (hybridRebootGateBlocked)
            {
                _logger.Info($"EnrollmentTracker: Completion source '{source}' blocked — hybrid join reboot gate " +
                             $"(waiting for IME completion or agent restart after ESP exit)");
                EmitCompletionCheck(source, "blocked", "hybrid_reboot_gate");
                return;
            }

            _logger.Info($"EnrollmentTracker: all guards passed for source '{source}' — emitting enrollment_complete");

            // Stop timers
            _summaryTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _summaryTimerActive = false;
            _debugStateTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _debugStateTimerActive = false;
            _deviceOnlyCompletionSafetyTimer?.Change(Timeout.Infinite, Timeout.Infinite);

            var helloOutcome = _espAndHelloTracker?.HelloOutcome ?? "unknown";

            // Snapshot timestamps under lock for consistent event data
            Dictionary<string, string> signalTimestamps;
            lock (_stateLock)
            {
                signalTimestamps = new Dictionary<string, string>();
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
                if (_stateData.DeviceSetupProvisioningCompleteUtc.HasValue)
                    signalTimestamps["deviceSetupProvisioningComplete"] = _stateData.DeviceSetupProvisioningCompleteUtc.Value.ToString("o");
            }

            // Capture ESP provisioning snapshot for the enrollment_complete event (all sources)
            Dictionary<string, object> espProvisioningData = null;
            if (_espAndHelloTracker != null)
            {
                var espSnapshot = _espAndHelloTracker.GetProvisioningSnapshot();
                if (espSnapshot != null)
                {
                    espProvisioningData = new Dictionary<string, object>
                    {
                        { "categoriesSeen", espSnapshot.CategoriesSeen },
                        { "categoriesResolved", espSnapshot.CategoriesResolved },
                        { "allResolved", espSnapshot.AllResolved },
                        { "categoryOutcomes", espSnapshot.CategoryOutcomes },
                        { "subcategoryStates", espSnapshot.SubcategoryStates }
                    };
                }
            }

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
                    { "autopilotMode", autopilotMode?.ToString() ?? "unknown" },
                    { "isSelfDeploying", isSelfDeploying },
                    { "isDeviceOnlyDeployment", isDeviceOnly },
                    { "signalsSeen", signalsSeen },
                    { "signalTimestamps", signalTimestamps },
                    { "agentUptimeSeconds", (DateTime.UtcNow - _agentStartTimeUtc).TotalSeconds },
                    { "espProvisioningStatus", espProvisioningData }
                },
                ImmediateUpload = true
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

            Dictionary<string, object> checkData;
            lock (_stateLock)
            {
                checkData = new Dictionary<string, object>
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
                    { "skipDeviceStatusPage", _skipDeviceStatusPage?.ToString() ?? "unknown" },
                    { "autopilotMode", _autopilotMode?.ToString() ?? "unknown" },
                    { "isSelfDeploying", IsSelfDeploying },
                    { "isDeviceOnlyDeployment", IsDeviceOnlyDeployment }
                };
            }

            _emitEvent(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = "completion_check",
                Severity = EventSeverity.Info,
                Source = "EnrollmentTracker",
                Phase = EnrollmentPhase.Unknown,
                Message = $"Completion source '{source}' evaluated — {result}",
                ImmediateUpload = true,
                Data = checkData
            });
        }

        /// <summary>
        /// Called by MonitoringService when Desktop Arrival is detected (explorer.exe under a real user).
        /// Corrects phase if needed, starts Hello wait timer in no-ESP scenarios, and attempts completion.
        /// </summary>
        public void NotifyDesktopArrived()
        {
            bool alreadyArrived;
            lock (_stateLock)
            {
                alreadyArrived = _desktopArrived;
                if (!alreadyArrived)
                {
                    _desktopArrived = true;
                    _stateData.DesktopArrived = true;
                    _stateData.DesktopArrivedUtc = DateTime.UtcNow;
                }
            }

            if (alreadyArrived)
            {
                _logger.Debug("EnrollmentTracker: NotifyDesktopArrived called but already arrived — ignoring");
                return;
            }

            _statePersistence.Save(_stateData); // Immediate persist — desktop arrival is completion-critical
            RecordSignal("desktop_arrived");
            _logger.Info("EnrollmentTracker: Desktop arrival notified");

            // Phase correction and SkipUserStatusPage handling when ESP was seen.
            // SkipUserStatusPage check is prioritized over phase correction because in WhiteGlove Part 2,
            // _lastEspPhase may already be "AccountSetup" (persisted from Part 1) which would otherwise
            // skip the SkipUser block and leave _espFinalExitSeen=false, blocking the desktop-arrival gate.
            bool espSeen;
            string previousPhase;
            bool? skipUserPage;
            bool espFinalExit;
            lock (_stateLock)
            {
                espSeen = _espEverSeen;
                previousPhase = _lastEspPhase ?? "unknown";
                skipUserPage = _skipUserStatusPage;
                espFinalExit = _espFinalExitSeen;
            }

            if (espSeen)
            {
                if (skipUserPage == true && !espFinalExit)
                {
                    lock (_stateLock)
                    {
                        _lastEspPhase = "FinalizingSetup";
                        _stateData.LastEspPhase = "FinalizingSetup";
                        _espFinalExitSeen = true;
                        _stateData.EspFinalExitSeen = true;
                        _stateData.EspFinalExitUtc = DateTime.UtcNow;
                        _hasAutoSwitchedToAppsPhase = false;
                    }
                    _statePersistence.Save(_stateData); // Immediate persist — EspFinalExitUtc critical for hybrid reboot gate
                    RecordSignal("desktop_arrived_skip_user");
                    _logger.Info($"EnrollmentTracker: Desktop arrival with SkipUserStatusPage=true — skipping AccountSetup, transitioning to FinalizingSetup (was: {previousPhase})");

                    _emitEvent(new EnrollmentEvent
                    {
                        SessionId = _sessionId,
                        TenantId = _tenantId,
                        EventType = "esp_phase_changed",
                        Severity = EventSeverity.Info,
                        Source = "DesktopArrivalDetector",
                        Phase = EnrollmentPhase.FinalizingSetup,
                        Message = $"ESP phase: FinalizingSetup (SkipUserStatusPage — desktop arrival, was: {previousPhase})",
                        Data = new Dictionary<string, object>
                        {
                            { "espPhase", "FinalizingSetup" },
                            { "autoDetected", true },
                            { "correctedBy", "desktop_arrival" },
                            { "previousPhase", previousPhase },
                            { "skipUserStatusPage", true }
                        },
                        ImmediateUpload = true
                    });

                    try { CollectDeviceInfoAtFinalizingSetup("desktop_arrived_skip_user"); }
                    catch (Exception ex) { _logger.Warning($"EnrollmentTracker: final device info collection failed (desktop_arrived_skip_user): {ex.Message}"); }
                }
                else if (!string.Equals(previousPhase, "AccountSetup", StringComparison.OrdinalIgnoreCase))
                {
                    // Normal flow: desktop arrival confirms AccountSetup phase
                    lock (_stateLock) { _lastEspPhase = "AccountSetup"; }
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
                        },
                        ImmediateUpload = true
                    });
                }
            }

            // Start Hello wait timer ONLY when ESP is NOT actively running.
            // During active ESP (AccountSetup runs in background with desktop visible),
            // the Hello timer must wait until ESP exits (started in OnFinalizingSetupPhaseTriggered).
            // Without this guard, Hello timeout-resolves while ESP still installs apps → premature completion.
            // Device-only deployment: no user session, Hello is irrelevant — skip timer entirely.
            bool espSeenNow, espFinalExitNow, isDeviceOnlyNow;
            lock (_stateLock)
            {
                espSeenNow = _espEverSeen;
                espFinalExitNow = _espFinalExitSeen;
                isDeviceOnlyNow = IsDeviceOnlyDeployment;
            }

            if (_espAndHelloTracker != null && !_espAndHelloTracker.IsHelloCompleted
                && (!espSeenNow || espFinalExitNow)
                && !isDeviceOnlyNow)
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
            string lastPhaseSnap;
            bool desktopArrSnap;
            lock (_stateLock)
            {
                lastPhaseSnap = _lastEspPhase;
                desktopArrSnap = _desktopArrived;
            }

            // AccountSetup started meanwhile? Timer is obsolete
            if (string.Equals(lastPhaseSnap, "AccountSetup", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Debug("EnrollmentTracker: Device-only ESP timer expired but AccountSetup detected — ignoring");
                return;
            }

            if (desktopArrSnap)
            {
                _logger.Info($"EnrollmentTracker: No AccountSetup phase after {DeviceOnlyEspTimerMinutes}min — classified as device-only ESP, desktop is active");
                lock (_stateLock)
                {
                    _espFinalExitSeen = true;
                    _stateData.EspFinalExitSeen = true;
                    _stateData.EspFinalExitUtc = DateTime.UtcNow;
                }
                _statePersistence.Save(_stateData); // Immediate persist — device-only ESP final exit
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
