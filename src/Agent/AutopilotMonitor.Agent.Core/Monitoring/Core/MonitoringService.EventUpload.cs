using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
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
            if (evt.Severity <= EventSeverity.Debug)
                _logger.Verbose($"Event emitted: {evt.EventType} - {evt.Message}");
            else
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

            // Track phase transitions for logging and gather rule notifications
            if (evt.Phase != _lastPhase)
            {
                // Unknown is an intermediate/reset state — log at Debug to reduce noise.
                // Real phase boundaries (DeviceSetup, AccountSetup, etc.) stay at Info.
                if (evt.Phase == EnrollmentPhase.Unknown)
                    _logger.Debug($"Phase transition: {_lastPhase?.ToString() ?? "null"} -> {evt.Phase}");
                else
                    _logger.Info($"Phase transition: {_lastPhase?.ToString() ?? "null"} -> {evt.Phase}");
                _lastPhase = evt.Phase;

                // Notify gather rule executor of phase change
                try { _gatherRuleExecutor?.OnPhaseChanged(evt.Phase); }
                catch (Exception ex) { _logger.Verbose($"GatherRuleExecutor.OnPhaseChanged failed: {ex.Message}"); }
            }

            // Notify gather rule executor of event type (for on_event triggers)
            if (!string.IsNullOrEmpty(evt.EventType))
            {
                try { _gatherRuleExecutor?.OnEvent(evt.EventType); }
                catch (Exception ex) { _logger.Verbose($"GatherRuleExecutor.OnEvent('{evt.EventType}') failed: {ex.Message}"); }
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

                // Always run the full completion sequence (analyzers, diagnostics, summary, exit).
                // HandleEnrollmentComplete conditionally self-destructs or reboots based on config.
                Task.Run(() => HandleEnrollmentComplete(enrollmentSucceeded));
                return; // Don't continue with normal event processing
            }

            // Immediate upload when explicitly requested by the emitter, or as a safety net for
            // unhandled errors. All other events batch via the debounce timer.
            if (evt.ImmediateUpload || evt.Severity >= EventSeverity.Error)
            {
                _logger.Info($"Triggering immediate upload for {evt.EventType} (bypassing debounce)");
                Task.Run(() => UploadEventsAsync());
            }
        }

        /// <summary>
        /// Emits a Trace-severity event from any component for agent decision auditing.
        /// Always logged locally; only sent to the backend when SendTraceEvents is enabled.
        /// </summary>
        private void EmitTraceEvent(string source, string decision, string reason, Dictionary<string, object> context = null)
        {
            _logger.Trace($"{source}: {decision} — {reason}");

            if (!_configuration.SendTraceEvents)
                return;

            var data = new Dictionary<string, object>
            {
                { "decision", decision },
                { "reason", reason }
            };
            if (context != null)
            {
                foreach (var kvp in context)
                    data[kvp.Key] = kvp.Value;
            }

            EmitEvent(new EnrollmentEvent
            {
                SessionId = _configuration.SessionId,
                TenantId = _configuration.TenantId,
                EventType = "agent_trace",
                Severity = EventSeverity.Trace,
                Source = source,
                Phase = EnrollmentPhase.Unknown,
                Message = $"{decision}: {reason}",
                Data = data
            });
        }

        /// <summary>
        /// Emits an agent_shutdown event as the counterpart to agent_started.
        /// Called at every controlled exit point so the timeline shows why and how the agent terminated.
        /// </summary>
        private void EmitShutdownEvent(string reason, string message, Dictionary<string, object> extraData = null)
        {
            var data = new Dictionary<string, object>
            {
                { "reason", reason },
                { "agentVersion", _agentVersion },
                { "uptimeMinutes", Math.Round((DateTime.UtcNow - _agentStartTimeUtc).TotalMinutes, 1) }
            };
            if (extraData != null)
            {
                foreach (var kvp in extraData)
                    data[kvp.Key] = kvp.Value;
            }

            EmitEvent(new EnrollmentEvent
            {
                SessionId = _configuration.SessionId,
                TenantId = _configuration.TenantId,
                Timestamp = DateTime.UtcNow,
                EventType = "agent_shutdown",
                Severity = EventSeverity.Info,
                Source = "Agent",
                Phase = _lastPhase ?? EnrollmentPhase.Unknown,
                Message = message,
                Data = data
            });
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

                if (response.DeviceKillSignal)
                {
                    _logger.Warning($"=== REMOTE KILL SIGNAL received from administrator. Spool: {_spool.GetCount()} events pending. Initiating self-destruct... ===");

                    // Stop all timers — no further uploads
                    _uploadTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                    _debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);

                    // Emit agent_shutdown event and try to upload it directly (we hold the upload
                    // semaphore so a normal UploadEventsAsync would skip — send inline instead)
                    EmitShutdownEvent(
                        "remote_kill_signal",
                        "Agent terminated by remote kill signal from administrator",
                        new Dictionary<string, object>
                        {
                            { "pendingEvents", _spool.GetCount() },
                            { "selfDestruct", true }
                        });
                    try
                    {
                        var shutdownEvents = _spool.GetBatch(_configuration.MaxBatchSize);
                        if (shutdownEvents.Count > 0)
                        {
                            await _apiClient.IngestEventsAsync(new IngestEventsRequest
                            {
                                SessionId = _configuration.SessionId,
                                TenantId = _configuration.TenantId,
                                Events = shutdownEvents
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning($"Failed to upload shutdown event before kill: {ex.Message}");
                    }

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

                if (!string.IsNullOrEmpty(response.AdminAction) && !_enrollmentTerminalEventSeen)
                {
                    var succeeded = string.Equals(response.AdminAction, "Succeeded", StringComparison.OrdinalIgnoreCase);
                    _logger.Warning($"=== ADMIN OVERRIDE: Session marked as {response.AdminAction} by administrator. Initiating cleanup... ===");

                    EmitEvent(new EnrollmentEvent
                    {
                        SessionId = _configuration.SessionId,
                        TenantId = _configuration.TenantId,
                        EventType = succeeded ? "enrollment_complete" : "enrollment_failed",
                        Severity = succeeded ? EventSeverity.Info : EventSeverity.Warning,
                        Source = "AdminOverride",
                        Phase = EnrollmentPhase.Complete,
                        Message = $"Session {response.AdminAction.ToLower()} by administrator — cleanup initiated",
                        Timestamp = DateTime.UtcNow,
                        Data = new Dictionary<string, object>
                        {
                            { "adminAction", response.AdminAction }
                        }
                    });
                    // EmitEvent triggers HandleEnrollmentComplete via the normal terminal event path
                    return;
                }

                if (response.Success)
                {
                    _spool.RemoveEvents(events);
                    _logger.Info($"Uploaded {response.EventsProcessed} events");

                    // Reset failure counters on success
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
        private void HandleAuthFailure(int httpStatusCode = 0)
        {
            _consecutiveAuthFailures++;

            if (_firstAuthFailureTime == null)
                _firstAuthFailureTime = DateTime.UtcNow;

            // Send distress signal on first auth failure (before potential agent shutdown).
            // The DistressReporter deduplicates: same error key is only sent once per session.
            if (_consecutiveAuthFailures == 1 && _distressReporter != null)
            {
                var distressType = httpStatusCode == 401
                    ? DistressErrorType.AuthCertificateRejected
                    : DistressErrorType.DeviceNotRegistered; // 403 = whitelist/device/tenant rejection

                _ = _distressReporter.TrySendAsync(distressType,
                    $"Backend returned {httpStatusCode} during event upload",
                    httpStatusCode: httpStatusCode);
            }

            _logger.Warning($"Authentication failure {_consecutiveAuthFailures}" +
                (_configuration.MaxAuthFailures > 0 ? $"/{_configuration.MaxAuthFailures}" : "") +
                $" (first failure at {_firstAuthFailureTime.Value:HH:mm:ss})");

            // Check max attempts (0 = disabled)
            if (_configuration.MaxAuthFailures > 0 && _consecutiveAuthFailures >= _configuration.MaxAuthFailures)
            {
                _logger.Error($"=== AGENT SHUTDOWN: {_consecutiveAuthFailures} consecutive authentication failures (401/403). " +
                    "The device is not authorized to send data to Autopilot Monitor. " +
                    "Check client certificate and Autopilot device validation in your tenant configuration. ===");

                // Emit shutdown event (stays in spool for diagnostics — upload not possible due to auth failure)
                EmitShutdownEvent(
                    "auth_failure",
                    $"Agent terminated: {_consecutiveAuthFailures} consecutive authentication failures exceeded threshold ({_configuration.MaxAuthFailures})",
                    new Dictionary<string, object>
                    {
                        { "consecutiveFailures", _consecutiveAuthFailures },
                        { "shutdownTrigger", "max_attempts" },
                        { "maxAuthFailures", _configuration.MaxAuthFailures }
                    });
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

                    // Emit shutdown event (stays in spool for diagnostics — upload not possible due to auth failure)
                    EmitShutdownEvent(
                        "auth_failure",
                        $"Agent terminated: authentication failures persisted for {elapsed.TotalMinutes:F0} minutes (timeout: {_configuration.AuthFailureTimeoutMinutes} min)",
                        new Dictionary<string, object>
                        {
                            { "consecutiveFailures", _consecutiveAuthFailures },
                            { "shutdownTrigger", "timeout" },
                            { "authFailureTimeoutMinutes", _configuration.AuthFailureTimeoutMinutes },
                            { "elapsedMinutes", Math.Round(elapsed.TotalMinutes, 1) }
                        });
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
                _logger.Info("===== ENROLLMENT COMPLETE - Starting shutdown sequence =====");

                // Step 1: Stop all event collectors
                _logger.Info("Stopping event collectors...");
                StopEventCollectors();
                _spool.StopWatching();

                // Step 1.5: Run shutdown analyzers to capture end-state (delta from startup).
                // In WhiteGlove Part 2, tag events so the backend merges findings with Part 1.
                RunShutdownAnalyzers(whiteGlovePart: _isWhiteGlovePart2 ? 2 : null);

                // Step 2: Upload all remaining events (includes analyzer findings)
                _logger.Info("Uploading final events...");
                await UploadEventsAsync();

                // Give a moment for final upload to complete
                await Task.Delay(2000);

                // Step 2.5: Upload diagnostics package (must complete before self-destruct/reboot)
                await UploadDiagnosticsWithTimelineEvents(enrollmentSucceeded);

                // Step 2.75: Launch enrollment summary dialog (fire-and-forget, runs independently)
                if (_configuration.ShowEnrollmentSummary)
                    LaunchEnrollmentSummaryDialog();

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
                        },
                        ImmediateUpload = true
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

                // Emit agent_shutdown as counterpart to agent_started
                EmitShutdownEvent(
                    enrollmentSucceeded ? "enrollment_complete" : "enrollment_failed",
                    $"Agent shutting down after enrollment {(enrollmentSucceeded ? "completion" : "failure")}",
                    new Dictionary<string, object>
                    {
                        { "enrollmentSucceeded", enrollmentSucceeded },
                        { "selfDestruct", _configuration.SelfDestructOnComplete },
                        { "reboot", _configuration.RebootOnComplete }
                    });
                await UploadEventsAsync();

                _logger.Info("Shutdown sequence complete. Agent will now exit.");

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

                // Step 0: Run shutdown analyzers to capture software inventory delta for pre-provisioning.
                // Tagged as Part 1 so the backend produces an initial vulnerability report.
                RunShutdownAnalyzers(whiteGlovePart: 1);

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

                // Step 4: Mark WhiteGlove Part 1 as complete so the next boot (Part 2)
                // can signal the backend to transition Pending → InProgress.
                _sessionPersistence.SaveWhiteGloveComplete();
                _logger.Info("WhiteGlove marker persisted for Part 2 detection");

                // Step 5: Exit — session.id + session.seq + whiteglove.complete survive so
                // the agent resumes on next boot.
                //         No DeleteSessionId, no WriteEnrollmentCompleteMarker, no self-destruct.
                //         The Windows Autopilot shutdown will power off the device;
                //         the Scheduled Task restarts the agent on the user's first boot.
                // Emit agent_shutdown as counterpart to agent_started
                EmitShutdownEvent(
                    "whiteglove_complete",
                    "Agent shutting down after WhiteGlove Part 1 — no cleanup, session preserved for Part 2 on next boot",
                    new Dictionary<string, object>
                    {
                        { "sessionPreserved", true },
                        { "selfDestruct", false },
                        { "reboot", false },
                        { "cleanup", false },
                        { "willResumeOnNextBoot", true }
                    });
                await UploadEventsAsync();

                // Step 6: Persist final sequence AFTER all events have been emitted and uploaded.
                // Must be the last mutation before exit so Part 2's LoadSequence() returns
                // the true final value — prevents duplicate sequence numbers across reboots.
                _sessionPersistence.SaveSequence(Interlocked.Read(ref _eventSequence));
                _logger.Info($"Sequence counter persisted at {Interlocked.Read(ref _eventSequence)} for next boot");

                _logger.Info("WhiteGlove graceful shutdown complete. Agent exiting.");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                _logger.Error("Error during WhiteGlove shutdown sequence", ex);
                Environment.Exit(0); // Still exit — don't leave a zombie process
            }
        }

        /// <summary>
        /// Copies the summary dialog EXE + dependencies + final-status.json to a temp folder
        /// and launches it in the user's desktop session via CreateProcessAsUser.
        /// Fire-and-forget: the agent does NOT wait for the dialog to exit.
        /// The dialog self-deletes its temp folder when closed.
        /// </summary>
        private void LaunchEnrollmentSummaryDialog()
        {
            try
            {
                var agentDir = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location) ?? string.Empty;
                var dialogExe = Path.Combine(agentDir, "AutopilotMonitor.SummaryDialog.exe");

                if (!File.Exists(dialogExe))
                {
                    _logger.Warning($"Summary dialog EXE not found at {dialogExe} — skipping");
                    return;
                }

                var statusFile = Path.Combine(
                    Environment.ExpandEnvironmentVariables(@"%ProgramData%\AutopilotMonitor\State"),
                    "final-status.json");

                if (!File.Exists(statusFile))
                {
                    _logger.Warning($"final-status.json not found at {statusFile} — skipping summary dialog");
                    return;
                }

                // Create temp folder for the dialog OUTSIDE the AutopilotMonitor folder.
                // The agent's self-destruct deletes ProgramData\AutopilotMonitor entirely,
                // so the dialog files must live elsewhere to survive.
                var tempDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "AutopilotMonitor-Summary");

                // Clean up any leftover folder from a previous run, then create fresh
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
                Directory.CreateDirectory(tempDir);

                // Grant the interactive user delete permissions so the dialog can self-cleanup
                GrantUserDeletePermission(tempDir);

                // Copy dialog EXE + Newtonsoft.Json.dll + final-status.json to temp
                var tempDialogExe = Path.Combine(tempDir, "AutopilotMonitor.SummaryDialog.exe");
                File.Copy(dialogExe, tempDialogExe);

                var jsonDll = Path.Combine(agentDir, "Newtonsoft.Json.dll");
                if (File.Exists(jsonDll))
                    File.Copy(jsonDll, Path.Combine(tempDir, "Newtonsoft.Json.dll"));

                var exeConfig = dialogExe + ".config";
                if (File.Exists(exeConfig))
                    File.Copy(exeConfig, tempDialogExe + ".config");

                var tempStatusFile = Path.Combine(tempDir, "final-status.json");
                File.Copy(statusFile, tempStatusFile);

                // Build command line arguments
                var args = $"--status-file \"{tempStatusFile}\" --timeout {_configuration.EnrollmentSummaryTimeoutSeconds} --cleanup";
                if (!string.IsNullOrEmpty(_configuration.EnrollmentSummaryBrandingImageUrl))
                    args += $" --branding-url \"{_configuration.EnrollmentSummaryBrandingImageUrl}\"";

                _logger.Info($"Launching enrollment summary dialog: {tempDialogExe} {args}");

                // Emit timeline event
                EmitEvent(new EnrollmentEvent
                {
                    SessionId = _configuration.SessionId,
                    TenantId = _configuration.TenantId,
                    Timestamp = DateTime.UtcNow,
                    EventType = "enrollment_summary_shown",
                    Severity = EventSeverity.Info,
                    Source = "MonitoringService",
                    Phase = EnrollmentPhase.Complete,
                    Message = "Launching enrollment summary dialog to user"
                });

                // Best-effort upload of the timeline event
                try { UploadEventsAsync().Wait(TimeSpan.FromSeconds(5)); } catch { }

                // Launch in user session (fire-and-forget)
                var launched = UserSessionProcessLauncher.LaunchInUserSession(
                    tempDialogExe, args, _logger,
                    _configuration.EnrollmentSummaryLaunchRetrySeconds);
                if (!launched)
                {
                    _logger.Warning("Could not launch summary dialog in user session (no interactive session found)");
                    // Clean up temp folder since dialog won't run
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to launch enrollment summary dialog: {ex.Message}");
                // Non-fatal — continue with self-destruct/reboot
            }
        }

        /// <summary>
        /// Grants the built-in Users group full control on a directory so the user-session
        /// SummaryDialog process can delete it during self-cleanup.
        /// </summary>
        private void GrantUserDeletePermission(string directoryPath)
        {
            try
            {
                var dirInfo = new DirectoryInfo(directoryPath);
                var security = dirInfo.GetAccessControl();
                var usersIdentity = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
                security.AddAccessRule(new FileSystemAccessRule(
                    usersIdentity,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                dirInfo.SetAccessControl(security);
            }
            catch (Exception ex)
            {
                _logger.Debug($"Could not set ACL on summary folder: {ex.Message}");
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
            _deliveryOptimizationCollector?.Dispose();
            _enrollmentTracker?.Dispose();
            _networkChangeDetector?.Dispose();
            _gatherRuleExecutor?.Dispose();
            _remoteConfigService?.Dispose();
            _distressReporter?.Dispose();
            _completionEvent.Dispose();
        }
    }
}

