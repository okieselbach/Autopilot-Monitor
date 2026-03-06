using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.Core.Configuration;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Agent.Core.Monitoring.Collectors;
using AutopilotMonitor.Agent.Core.Monitoring.Network;
using AutopilotMonitor.Agent.Core.Monitoring.Tracking;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.Core.Monitoring.Core
{
    /// <summary>
    /// Partial: Event emission, upload orchestration, auth failure handling,
    /// enrollment/WhiteGlove completion, and diagnostics upload.
    /// </summary>
    public partial class MonitoringService
    {
        public void EmitEvent(EnrollmentEvent evt)
        {
            evt.Sequence = Interlocked.Increment(ref _eventSequence);
            _spool.Add(evt);
            _logger.Info($"Event emitted: {evt.EventType} - {evt.Message}");

            // Track real enrollment activity for idle timeout
            if (!string.IsNullOrEmpty(evt.EventType) && !IsPeriodicEvent(evt.EventType))
            {
                _lastRealEventTime = DateTime.UtcNow;

                // Restart periodic collectors if they were stopped due to idle
                if (_collectorsIdleStopped)
                {
                    RestartPeriodicCollectors();
                }
            }

            // Persist sequence periodically (every 50 events) + always on critical events
            if (evt.Sequence % 50 == 0 ||
                evt.EventType == "whiteglove_complete" ||
                evt.EventType == "enrollment_complete" ||
                evt.EventType == "enrollment_failed")
            {
                _sessionPersistence.SaveSequence(evt.Sequence);
            }

            // Cancel max lifetime timer on terminal events
            if (evt.EventType == "whiteglove_complete" ||
                evt.EventType == "enrollment_complete" ||
                evt.EventType == "enrollment_failed")
            {
                _enrollmentTerminalEventSeen = true;
                _maxLifetimeTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            }

            // Check if this is a phase transition
            bool isPhaseTransition = false;
            if (evt.Phase != _lastPhase)
            {
                _logger.Info($"Phase transition: {_lastPhase?.ToString() ?? "null"} -> {evt.Phase}");
                _lastPhase = evt.Phase;
                isPhaseTransition = true;

                // Notify gather rule executor of phase change
                try { _gatherRuleExecutor?.OnPhaseChanged(evt.Phase); } catch { }
            }

            // Notify gather rule executor of event type (for on_event triggers)
            if (!string.IsNullOrEmpty(evt.EventType))
            {
                try { _gatherRuleExecutor?.OnEvent(evt.EventType); } catch { }
            }

            // Check for WhiteGlove completion — agent exits gracefully, session stays open
            if (evt.EventType == "whiteglove_complete")
            {
                _logger.Info("WhiteGlove pre-provisioning complete. Starting graceful shutdown sequence.");

                // Stop performance collection immediately (no value while device is off)
                _performanceCollector?.Stop();
                _performanceCollector?.Dispose();
                _performanceCollector = null;

                // Stop all event collectors and the spool watcher — no new events after this point
                StopEventCollectors();
                _spool.StopWatching();

                // Stop upload timers — we drive uploads manually from here
                _uploadTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                _debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);

                Task.Run(() => HandleWhiteGloveComplete());

                return; // Don't continue with normal event processing
            }

            // Check for enrollment completion events
            if (evt.EventType == "enrollment_complete" || evt.EventType == "enrollment_failed")
            {
                var enrollmentSucceeded = evt.EventType == "enrollment_complete";
                _logger.Info($"Enrollment completion detected: {evt.EventType}");

                // Delete session ID so a new session will be created on next enrollment
                DeleteSessionId();

                // Stop ALL collectors to minimize system impact
                _logger.Info("Stopping all collectors after enrollment completion...");
                StopEventCollectors();

                // Stop upload timers - no more periodic uploads needed
                _uploadTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                _debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);

                // Final upload to flush any remaining events
                Task.Run(async () =>
                {
                    try
                    {
                        await UploadEventsAsync();
                        _logger.Info("Final event upload after enrollment completion done");
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning($"Final upload after enrollment completion failed: {ex.Message}");
                    }
                });

                // Trigger self-destruct or reboot if configured
                if (_configuration.SelfDestructOnComplete || _configuration.RebootOnComplete)
                {
                    Task.Run(() => HandleEnrollmentComplete(enrollmentSucceeded));
                    return; // Don't continue with normal event processing
                }

                // No self-destruct/reboot — still upload diagnostics if configured
                Task.Run(() => UploadDiagnosticsWithTimelineEvents(enrollmentSucceeded));
            }

            // Immediate upload for:
            // 1. Critical events (errors) - for troubleshooting
            // 2. Phase transitions (start/end) - for real-time phase tracking in UI
            // 3. Events with "phase" in EventType - explicit phase-related events
            // 4. App download/install events - for real-time download progress UI updates
            var isAppEvent = evt.EventType?.StartsWith("app_", StringComparison.OrdinalIgnoreCase) == true;

            if (evt.Severity >= EventSeverity.Error ||
                isPhaseTransition ||
                evt.EventType?.Contains("phase", StringComparison.OrdinalIgnoreCase) == true ||
                isAppEvent)
            {
                _logger.Info($"Triggering immediate upload for {evt.EventType} (bypassing debounce)");
                Task.Run(() => UploadEventsAsync());
            }
        }

        /// <summary>
        /// Called when new events are available in the spool (FileSystemWatcher detected new files)
        /// Uses debouncing to batch events before uploading
        /// </summary>
        private void OnEventsAvailable(object sender, EventArgs e)
        {
            _logger.Debug("FileSystemWatcher detected new events, starting/resetting debounce timer");

            // Reset debounce timer - wait for batch window before uploading
            // This allows multiple events to accumulate, reducing API calls
            lock (_timerLock)
            {
                _debounceTimer.Change(
                    TimeSpan.FromSeconds(_configuration.UploadIntervalSeconds),
                    Timeout.InfiniteTimeSpan
                );
            }
        }

        /// <summary>
        /// Called when debounce timer expires - uploads batched events
        /// </summary>
        private void DebounceTimerCallback(object state)
        {
            _logger.Debug("Debounce timer expired, uploading batched events");
            Task.Run(() => UploadEventsAsync());
        }

        /// <summary>
        /// Fallback timer callback in case FileSystemWatcher misses events
        /// </summary>
        private void UploadTimerCallback(object state)
        {
            Task.Run(() => UploadEventsAsync());
        }

        private async Task UploadEventsAsync()
        {
            // Prevent concurrent uploads — if one is already in flight, skip this call.
            // The events will be picked up by the next trigger or debounce timer.
            if (!_uploadSemaphore.Wait(0))
                return;

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
                    _logger.Warning("=== REMOTE KILL SIGNAL received from administrator. Initiating self-destruct... ===");

                    // Stop all timers — no further uploads
                    _uploadTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                    _debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);

                    // Force self-destruct regardless of current config, respecting KeepLogFile from tenant config
                    _configuration.SelfDestructOnComplete = true;
                    _cleanupService.ExecuteSelfDestruct();
                    Environment.Exit(0);
                    return;
                }

                if (response.DeviceBlocked)
                {
                    var unblockMsg = response.UnblockAt.HasValue
                        ? $" until {response.UnblockAt.Value:yyyy-MM-dd HH:mm:ss} UTC"
                        : string.Empty;
                    _logger.Warning($"=== DEVICE BLOCKED by administrator{unblockMsg}. Stopping all uploads for this session. ===");

                    // Stop upload timers — no further uploads for the duration of this run.
                    // The block is temporary; the next agent run (after reboot / re-enrollment) will check again.
                    _uploadTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                    _debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                    return;
                }

                if (response.Success)
                {
                    _spool.RemoveEvents(events);
                    _logger.Info($"Uploaded {response.EventsProcessed} events");

                    // Reset failure counters on success
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
                HandleAuthFailure();
            }
            catch (Exception ex)
            {
                _consecutiveUploadFailures++;
                _logger.Error("Error uploading events", ex);

                // After N consecutive non-auth failures, signal the emergency channel once.
                // The EmergencyReporter deduplicates further calls for the same error category.
                if (_consecutiveUploadFailures == EmergencyReporter.ConsecutiveFailureThreshold)
                {
                    _ = _emergencyReporter.TrySendAsync(
                        AgentErrorType.IngestFailed,
                        ex.Message,
                        httpStatusCode: null,
                        sequenceNumber: _eventSequence);
                }
            }
            finally
            {
                _uploadSemaphore.Release();
            }
        }

        /// <summary>
        /// Handles an authentication failure by incrementing the counter and shutting down
        /// the agent if the configured threshold is reached.
        /// </summary>
        private void HandleAuthFailure()
        {
            _consecutiveAuthFailures++;

            if (_firstAuthFailureTime == null)
                _firstAuthFailureTime = DateTime.UtcNow;

            _logger.Warning($"Authentication failure {_consecutiveAuthFailures}" +
                (_configuration.MaxAuthFailures > 0 ? $"/{_configuration.MaxAuthFailures}" : "") +
                $" (first failure at {_firstAuthFailureTime.Value:HH:mm:ss})");

            // Check max attempts (0 = disabled)
            if (_configuration.MaxAuthFailures > 0 && _consecutiveAuthFailures >= _configuration.MaxAuthFailures)
            {
                _logger.Error($"=== AGENT SHUTDOWN: {_consecutiveAuthFailures} consecutive authentication failures (401/403). " +
                    "The device is not authorized to send data to Autopilot Monitor. " +
                    "Check client certificate and Autopilot device validation in your tenant configuration. ===");
                Environment.Exit(1);
            }

            // Check timeout (0 = disabled)
            if (_configuration.AuthFailureTimeoutMinutes > 0 && _firstAuthFailureTime.HasValue)
            {
                var elapsed = DateTime.UtcNow - _firstAuthFailureTime.Value;
                if (elapsed.TotalMinutes >= _configuration.AuthFailureTimeoutMinutes)
                {
                    _logger.Error($"=== AGENT SHUTDOWN: Authentication failures persisted for {elapsed.TotalMinutes:F0} minutes " +
                        $"(timeout: {_configuration.AuthFailureTimeoutMinutes} min). " +
                        "The device is not authorized to send data to Autopilot Monitor. " +
                        "Check client certificate and Autopilot device validation in your tenant configuration. ===");
                    Environment.Exit(1);
                }
            }
        }

        /// <summary>
        /// Deletes the persisted session ID and sequence files.
        /// Should be called when enrollment is complete/failed.
        /// </summary>
        private void DeleteSessionId()
        {
            _logger.Info("Deleting persisted session ID and sequence...");
            try
            {
                _sessionPersistence.DeleteSession(); // Deletes session.id AND session.seq
                _logger.Info("Session ID and sequence deleted successfully");
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to delete session ID: {ex.Message}");
            }
        }

        /// <summary>
        /// Uploads diagnostics package with timeline events (start + result).
        /// Emits events, uploads them, then performs the actual diagnostics upload.
        /// </summary>
        private async Task UploadDiagnosticsWithTimelineEvents(bool enrollmentSucceeded, string fileNameSuffix = null)
        {
            var mode = _configuration.DiagnosticsUploadMode ?? "Off";
            if (string.Equals(mode, "Off", StringComparison.OrdinalIgnoreCase))
                return;
            if (!_configuration.DiagnosticsUploadEnabled)
                return;
            if (string.Equals(mode, "OnFailure", StringComparison.OrdinalIgnoreCase) && enrollmentSucceeded)
                return;

            // Emit "collecting" event
            EmitEvent(new EnrollmentEvent
            {
                SessionId = _configuration.SessionId,
                TenantId = _configuration.TenantId,
                Timestamp = DateTime.UtcNow,
                EventType = "diagnostics_collecting",
                Severity = EventSeverity.Info,
                Source = "MonitoringService",
                Phase = EnrollmentPhase.Complete,
                Message = $"Collecting diagnostics package (mode={mode}{(fileNameSuffix != null ? $", type={fileNameSuffix}" : "")})",
                Data = new Dictionary<string, object>
                {
                    { "uploadMode", mode }
                }
            });
            await UploadEventsAsync();

            // Perform the actual ZIP + upload
            var uploadResult = await _diagnosticsService.CreateAndUploadAsync(enrollmentSucceeded, fileNameSuffix);
            var success = uploadResult?.Success == true;

            // Emit result event (includes blobName so backend can store it on the session)
            var resultData = new Dictionary<string, object>
            {
                { "uploadMode", mode }
            };
            if (uploadResult?.BlobName != null)
                resultData["blobName"] = uploadResult.BlobName;
            if (uploadResult?.SasUrlPrefix != null)
                resultData["sasUrl"] = uploadResult.SasUrlPrefix;
            if (uploadResult?.ErrorCode != null)
                resultData["errorCode"] = uploadResult.ErrorCode;

            EmitEvent(new EnrollmentEvent
            {
                SessionId = _configuration.SessionId,
                TenantId = _configuration.TenantId,
                Timestamp = DateTime.UtcNow,
                EventType = success ? "diagnostics_uploaded" : "diagnostics_upload_failed",
                Severity = success ? EventSeverity.Info : EventSeverity.Warning,
                Source = "MonitoringService",
                Phase = EnrollmentPhase.Complete,
                Message = success
                    ? $"Diagnostics package uploaded: {uploadResult.BlobName}"
                    : $"Diagnostics package upload failed{(uploadResult?.ErrorCode != null ? $": {uploadResult.ErrorCode}" : "")}",
                Data = resultData
            });
            await UploadEventsAsync();
        }

        /// <summary>
        /// Handles enrollment completion and triggers self-destruct sequence
        /// </summary>
        private async Task HandleEnrollmentComplete(bool enrollmentSucceeded)
        {
            try
            {
                _logger.Info("===== ENROLLMENT COMPLETE - Starting Self-Destruct Sequence =====");

                // Step 1: Stop all event collectors
                _logger.Info("Stopping event collectors...");
                StopEventCollectors();
                _spool.StopWatching();

                // Step 1.5: Run shutdown analyzers to capture end-state (delta from startup)
                RunShutdownAnalyzers();

                // Step 2: Upload all remaining events (includes analyzer findings)
                _logger.Info("Uploading final events...");
                await UploadEventsAsync();

                // Give a moment for final upload to complete
                await Task.Delay(2000);

                // Step 2.5: Upload diagnostics package (must complete before self-destruct/reboot)
                await UploadDiagnosticsWithTimelineEvents(enrollmentSucceeded);

                // Step 3: Self-destruct (removes Scheduled Task + files) or reboot-only
                if (_configuration.SelfDestructOnComplete)
                {
                    _cleanupService.ExecuteSelfDestruct();
                }
                else if (_configuration.RebootOnComplete)
                {
                    _logger.Info($"RebootOnComplete enabled - initiating reboot in {_configuration.RebootDelaySeconds}s");

                    // Emit reboot event into the timeline before shutdown
                    EmitEvent(new EnrollmentEvent
                    {
                        SessionId = _configuration.SessionId,
                        TenantId = _configuration.TenantId,
                        Timestamp = DateTime.UtcNow,
                        EventType = "reboot_triggered",
                        Severity = EventSeverity.Info,
                        Source = "MonitoringService",
                        Phase = EnrollmentPhase.Complete,
                        Message = $"Reboot configured — triggering reboot in {_configuration.RebootDelaySeconds} seconds",
                        Data = new Dictionary<string, object>
                        {
                            { "rebootDelaySeconds", _configuration.RebootDelaySeconds }
                        }
                    });

                    // Final upload to ensure the reboot event is sent
                    await UploadEventsAsync();

                    var psi = new ProcessStartInfo
                    {
                        FileName = "shutdown.exe",
                        Arguments = $"/r /t {_configuration.RebootDelaySeconds} /c \"Autopilot enrollment completed - rebooting\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    Process.Start(psi);
                }

                _logger.Info("Self-destruct sequence initiated. Agent will now exit.");

                // Exit the application
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                _logger.Error("Error during self-destruct sequence", ex);
                Environment.Exit(1);
            }
        }

        /// <summary>
        /// Graceful shutdown sequence after WhiteGlove (Pre-Provisioning) completes.
        /// Mirrors HandleEnrollmentComplete but does NOT delete the session ID or write the
        /// enrollment-complete marker — the session must survive for Part 2 (user enrollment).
        /// Uploads a "preprov" diagnostics package if diagnostics are configured.
        /// </summary>
        private async Task HandleWhiteGloveComplete()
        {
            try
            {
                _logger.Info("===== WHITEGLOVE COMPLETE - Starting graceful shutdown sequence =====");

                // Step 1: Drain the entire spool — whiteglove_complete may be beyond the first
                //         MaxBatchSize (100) events, so loop until empty.
                _logger.Info("Uploading final events (draining spool)...");
                int maxIterations = 20; // safety cap against persistent upload failures
                while (_spool.GetCount() > 0 && maxIterations-- > 0)
                {
                    await UploadEventsAsync();
                }

                // Step 2: Small delay to ensure final network transmission completes
                await Task.Delay(2000);

                // Step 3: Upload pre-provisioning diagnostics package if configured.
                //         Pass enrollmentSucceeded=true (pre-prov succeeded) and suffix "preprov"
                //         so the blob is named AgentDiagnostics-{sessionId}-{ts}-preprov.zip,
                //         distinguishable from the later full-enrollment package.
                await UploadDiagnosticsWithTimelineEvents(enrollmentSucceeded: true, fileNameSuffix: "preprov");

                // Step 4: Persist final sequence for Boot 2 continuation
                _sessionPersistence.SaveSequence(Interlocked.Read(ref _eventSequence));
                _logger.Info($"Sequence counter persisted at {Interlocked.Read(ref _eventSequence)} for next boot");

                // Step 5: Mark WhiteGlove Part 1 as complete so the next boot (Part 2)
                // can signal the backend to transition Pending → InProgress.
                _sessionPersistence.SaveWhiteGloveComplete();
                _logger.Info("WhiteGlove marker persisted for Part 2 detection");

                // Step 6: Exit — session.id + session.seq + whiteglove.complete survive so
                // the agent resumes on next boot.
                //         No DeleteSessionId, no WriteEnrollmentCompleteMarker, no self-destruct.
                //         The Windows Autopilot shutdown will power off the device;
                //         the Scheduled Task restarts the agent on the user's first boot.
                _logger.Info("WhiteGlove graceful shutdown complete. Agent exiting.");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                _logger.Error("Error during WhiteGlove shutdown sequence", ex);
                Environment.Exit(0); // Still exit — don't leave a zombie process
            }
        }

        public void Dispose()
        {
            _uploadTimer?.Dispose();
            _debounceTimer?.Dispose();
            _idleCheckTimer?.Dispose();
            _apiClient?.Dispose();
            _spool?.Dispose();
            _espAndHelloTracker?.Dispose();
            _logReplay?.Dispose();
            _performanceCollector?.Dispose();
            _agentSelfMetricsCollector?.Dispose();
            _enrollmentTracker?.Dispose();
            _gatherRuleExecutor?.Dispose();
            _remoteConfigService?.Dispose();
            _completionEvent.Dispose();
        }
    }
}

