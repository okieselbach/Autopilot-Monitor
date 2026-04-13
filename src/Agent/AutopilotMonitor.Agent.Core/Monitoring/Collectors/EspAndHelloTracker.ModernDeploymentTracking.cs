using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.Core.Monitoring.Collectors
{
    /// <summary>
    /// Partial: ModernDeployment-Diagnostics-Provider event log handling (Ebene 1 — live capture + backfill).
    ///
    /// Subscribes to two Windows event channels that Microsoft uses to log Autopilot and ESP events:
    ///   - Microsoft-Windows-ModernDeployment-Diagnostics-Provider/Autopilot
    ///   - Microsoft-Windows-ModernDeployment-Diagnostics-Provider/ManagementService
    ///
    /// Current mode is **live-capture + targeted backfill**: every event at Level ≤ ModernDeploymentLogLevelMax
    /// (default 3 = Warning, Error, Critical) is forwarded to the backend as a
    /// <see cref="Constants.EventTypes.ModernDeploymentLog"/>/<see cref="Constants.EventTypes.ModernDeploymentWarning"/>/
    /// <see cref="Constants.EventTypes.ModernDeploymentError"/> event. Targeted events (e.g. Event 509)
    /// are additionally backfilled from the event log on startup to catch events that were written
    /// before the agent started (e.g. WhiteGlove initiation during OOBE before MDM enroll).
    ///
    /// We intentionally do NOT classify failures locally or fire <c>EspFailureDetected</c> from this
    /// code yet — we first want to gather real EventIDs from production devices and then iterate on
    /// classification rules via backend config without agent rollout.
    ///
    /// Timing: Watchers stay subscribed for the entire agent lifetime (not stopped by idle timeout).
    /// They are push-based (kernel-driven delivery) — zero-cost when no events are written.
    /// </summary>
    public partial class EspAndHelloTracker
    {
        private const string ModernDeploymentAutopilotChannel = "Microsoft-Windows-ModernDeployment-Diagnostics-Provider/Autopilot";
        private const string ModernDeploymentManagementChannel = "Microsoft-Windows-ModernDeployment-Diagnostics-Provider/ManagementService";

        /// <summary>
        /// ManagementService Event ID 509: "AutopilotManager enabled TPM requirement due to WhiteGlove policy value 1".
        /// Fires only when a technician actually initiates pre-provisioning (Win 5x → Provision),
        /// NOT when the profile merely allows it (AutopilotMode=1). This is the earliest reliable
        /// indicator that a session is running in WhiteGlove mode.
        /// Level 4 (Informational) — bypasses the default Level≤3 filter, included via targeted EventID.
        /// </summary>
        private const int EventId_ManagementService_WhiteGloveStart = 509;

        /// <summary>
        /// Event IDs to capture from ManagementService regardless of the level filter.
        /// These are included in the XPath via "or (EventID=X or EventID=Y ...)" so they
        /// bypass the general Level≤ModernDeploymentLogLevelMax threshold.
        /// To observe additional events, add their IDs here — no new watcher needed.
        /// </summary>
        private static readonly HashSet<int> TargetedManagementServiceEventIds = new HashSet<int>
        {
            EventId_ManagementService_WhiteGloveStart  // 509
        };

        private System.Diagnostics.Eventing.Reader.EventLogWatcher _modernDeploymentAutopilotWatcher;
        private System.Diagnostics.Eventing.Reader.EventLogWatcher _modernDeploymentManagementWatcher;

        /// <summary>
        /// File name for persisting WhiteGlove backfill state across agent restarts.
        /// Written to the stateDirectory passed via the constructor.
        /// </summary>
        private const string WhiteGloveBackfillStateFileName = "whiteglove-backfill.json";

        private void StartModernDeploymentEventLogWatchers()
        {
            _modernDeploymentAutopilotWatcher = TryStartModernDeploymentWatcher(
                ModernDeploymentAutopilotChannel, "Autopilot");
            _modernDeploymentManagementWatcher = TryStartModernDeploymentWatcher(
                ModernDeploymentManagementChannel, "ManagementService", TargetedManagementServiceEventIds);
        }

        private System.Diagnostics.Eventing.Reader.EventLogWatcher TryStartModernDeploymentWatcher(
            string channelName, string shortName, HashSet<int> targetedEventIds = null)
        {
            try
            {
                // Clamp the level filter into the valid Windows range [1..5].
                var levelMax = Math.Max(1, Math.Min(5, _modernDeploymentLogLevelMax));

                // XPath: (Level >= 1 and Level <= levelMax). Level 0 ("LogAlways") is not filtered out by
                // XPath comparison — Windows treats it separately — so we explicitly include it in the query.
                // Targeted event IDs (e.g. Event 509 at Level 4) are added as an OR clause so they
                // bypass the level filter — no dedicated watcher needed per event.
                var levelFilter = $"Level=0 or (Level >= 1 and Level <= {levelMax})";

                if (targetedEventIds != null && targetedEventIds.Count > 0)
                {
                    var idClauses = string.Join(" or ", System.Linq.Enumerable.Select(targetedEventIds, id => $"EventID={id}"));
                    levelFilter += $" or ({idClauses})";
                }

                var xpath = $"*[System[{levelFilter}]]";

                var query = new EventLogQuery(channelName, PathType.LogName, xpath);
                var watcher = new System.Diagnostics.Eventing.Reader.EventLogWatcher(query);
                watcher.EventRecordWritten += (sender, args) => OnModernDeploymentEventRecordWritten(args, shortName, channelName);
                watcher.Enabled = true;

                _logger.Info($"ModernDeployment watcher started: {channelName} (levelMax={levelMax}, targetedIds={targetedEventIds?.Count ?? 0})");
                return watcher;
            }
            catch (EventLogNotFoundException)
            {
                _logger.Warning($"ModernDeployment event log not found: {channelName} (normal on non-Windows 10/11 test environments)");
                return null;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.Warning($"ModernDeployment watcher access denied for {channelName}: {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to start ModernDeployment watcher for {channelName}", ex);
                return null;
            }
        }

        private void StopModernDeploymentEventLogWatchers()
        {
            StopModernDeploymentWatcher(ref _modernDeploymentAutopilotWatcher, "Autopilot");
            StopModernDeploymentWatcher(ref _modernDeploymentManagementWatcher, "ManagementService");
        }

        private void StopModernDeploymentWatcher(ref System.Diagnostics.Eventing.Reader.EventLogWatcher watcher, string shortName)
        {
            if (watcher == null)
                return;

            try
            {
                watcher.Enabled = false;
                watcher.Dispose();
                _logger.Info($"ModernDeployment watcher stopped: {shortName}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Error stopping ModernDeployment watcher ({shortName})", ex);
            }
            finally
            {
                watcher = null;
            }
        }

        // -----------------------------------------------------------------------
        // Live event handler (EventLogWatcher callback)
        // -----------------------------------------------------------------------

        private void OnModernDeploymentEventRecordWritten(EventRecordWrittenEventArgs e, string shortName, string channelName)
        {
            if (e.EventRecord == null)
                return;

            try
            {
                ProcessModernDeploymentRecord(e.EventRecord, shortName, channelName, isBackfill: false);
            }
            catch (Exception ex)
            {
                _logger.Error($"Error processing ModernDeployment event from {shortName}", ex);
            }
        }

        // -----------------------------------------------------------------------
        // Shared event processing (used by both live watcher and backfill reader)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Processes a single ModernDeployment event record. Called from both the live
        /// EventLogWatcher callback and the startup backfill reader. The <paramref name="isBackfill"/>
        /// flag is included in the emitted event data for trace analysis.
        /// </summary>
        private void ProcessModernDeploymentRecord(EventRecord record, string shortName, string channelName, bool isBackfill)
        {
            // Targeted event dispatch: WhiteGlove start (Event 509, Level 4).
            // Captured via the combined XPath filter — handle before the generic level switch.
            if (record.Id == EventId_ManagementService_WhiteGloveStart
                && shortName == "ManagementService")
            {
                HandleWhiteGloveStartEvent(record, isBackfill);
                return;
            }

            // Determine severity based on Windows event level:
            //   1 = Critical, 2 = Error, 3 = Warning, 4 = Information, 5 = Verbose
            var level = record.Level ?? 4;
            string eventType;
            EventSeverity severity;
            switch (level)
            {
                case 1:
                case 2:
                    eventType = Constants.EventTypes.ModernDeploymentError;
                    severity = EventSeverity.Error;
                    break;
                case 3:
                    eventType = Constants.EventTypes.ModernDeploymentWarning;
                    severity = EventSeverity.Warning;
                    break;
                default:
                    eventType = Constants.EventTypes.ModernDeploymentLog;
                    severity = EventSeverity.Info;
                    break;
            }

            // Downgrade known harmless warnings to Debug.
            // EventID 100 Level 3 = "Autopilot policy not found" — fires when optional
            // ESP sync policies are not configured; no real impact on enrollment.
            if (level == 3 && record.Id == 100)
            {
                eventType = Constants.EventTypes.ModernDeploymentLog;
                severity = EventSeverity.Debug;
            }

            string description = null;
            try { description = record.FormatDescription(); }
            catch { /* some events lack formatting resources */ }

            if (string.IsNullOrEmpty(description))
                description = $"Event ID {record.Id} (no formatted description)";

            // Trim the message body to keep the event payload small.
            var truncated = description.Length > 1000 ? description.Substring(0, 1000) + "…" : description;

            var data = new Dictionary<string, object>
            {
                { "channel", shortName },
                { "channelFullName", channelName },
                { "eventId", record.Id },
                { "level", level },
                { "levelName", record.LevelDisplayName ?? string.Empty },
                { "providerName", record.ProviderName ?? string.Empty },
                { "timeCreated", record.TimeCreated?.ToUniversalTime().ToString("o") ?? string.Empty },
                { "backfilled", isBackfill }
            };

            _onEventCollected(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = eventType,
                Severity = severity,
                Source = "ModernDeploymentWatcher",
                Phase = EnrollmentPhase.Unknown,
                Message = $"[{shortName}] EventID {record.Id}: {truncated}",
                Data = data
            });
        }

        // -----------------------------------------------------------------------
        // WhiteGlove start detection (Event 509)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Handles ManagementService Event 509 (WhiteGlove start indicator).
        /// Called from <see cref="ProcessModernDeploymentRecord"/> when a targeted
        /// event ID matches. Emits a whiteglove_started event for monitoring — no actions derived.
        /// Persists the detection to disk so subsequent agent restarts skip the backfill scan.
        /// </summary>
        private void HandleWhiteGloveStartEvent(EventRecord record, bool isBackfill)
        {
            string description = null;
            try { description = record.FormatDescription(); }
            catch { }

            if (string.IsNullOrEmpty(description))
                description = $"Event ID {record.Id} (no formatted description)";

            // Only emit if description confirms WhiteGlove context (guard against
            // future reuse of Event 509 for unrelated purposes).
            if (description.IndexOf("WhiteGlove", StringComparison.OrdinalIgnoreCase) < 0)
            {
                _logger.Trace($"ManagementService Event 509 without WhiteGlove keyword — ignoring: {description}");
                return;
            }

            // Fire-once guard: emit only the first occurrence per agent lifetime.
            lock (_stateLock)
            {
                if (_whiteGloveStartDetected) return;
                _whiteGloveStartDetected = true;
            }

            _logger.Info($"WhiteGlove start detected via ManagementService Event 509 (backfill={isBackfill}): {description}");

            // Persist across restarts — subsequent agent launches skip the backfill scan.
            PersistWhiteGloveBackfillState(record.TimeCreated?.ToUniversalTime());

            var truncated = description.Length > 500 ? description.Substring(0, 500) + "…" : description;

            _onEventCollected(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = "whiteglove_started",
                Severity = EventSeverity.Info,
                Source = "ModernDeploymentWatcher",
                Phase = EnrollmentPhase.Unknown,
                Message = $"WhiteGlove (Pre-Provisioning) initiated — ManagementService EventID {record.Id}",
                Data = new Dictionary<string, object>
                {
                    { "channel", "ManagementService" },
                    { "channelFullName", ModernDeploymentManagementChannel },
                    { "eventId", record.Id },
                    { "level", record.Level ?? 4 },
                    { "description", truncated },
                    { "timeCreated", record.TimeCreated?.ToUniversalTime().ToString("o") ?? string.Empty },
                    { "backfilled", isBackfill }
                },
                ImmediateUpload = true
            });
        }

        // -----------------------------------------------------------------------
        // Targeted backfill (runs once on startup)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Scans the ManagementService event log for targeted events (e.g. Event 509) that may
        /// have been written before the agent started. This covers the timing gap during OOBE where
        /// WhiteGlove initiation (Event 509) fires before MDM enrollment completes and the agent
        /// starts monitoring.
        ///
        /// <para>Flow:</para>
        /// <list type="number">
        ///   <item>Check persisted state — if WhiteGlove was already seen in a prior run, skip the scan
        ///         and set the in-memory guard to suppress live duplicates.</item>
        ///   <item>If not persisted, query the event log with a time-bounded XPath filter for the
        ///         configured lookback window (default: 30 minutes).</item>
        ///   <item>Process each matching event through <see cref="ProcessModernDeploymentRecord"/> with
        ///         <c>isBackfill=true</c>. The existing fire-once guard in
        ///         <see cref="HandleWhiteGloveStartEvent"/> prevents duplicate emissions.</item>
        /// </list>
        /// </summary>
        private void BackfillTargetedModernDeploymentEvents()
        {
            if (!_modernDeploymentBackfillEnabled)
            {
                _logger.Info("ModernDeployment backfill disabled by config");
                return;
            }

            if (TargetedManagementServiceEventIds.Count == 0)
                return;

            // Check persisted state from a prior agent run — if WhiteGlove was already detected,
            // skip the event log scan entirely and set the in-memory guard.
            var persistedState = LoadWhiteGloveBackfillState();
            if (persistedState != null && persistedState.WhiteGloveStartSeen)
            {
                _logger.Info($"WhiteGlove start already persisted from prior run (seen at {persistedState.SeenUtc:O}) — skipping backfill");
                lock (_stateLock) { _whiteGloveStartDetected = true; }
                return;
            }

            try
            {
                var lookbackMs = _modernDeploymentBackfillLookbackMinutes * 60 * 1000;
                var idClauses = string.Join(" or ",
                    System.Linq.Enumerable.Select(TargetedManagementServiceEventIds, id => $"EventID={id}"));
                var xpath = $"*[System[({idClauses}) and TimeCreated[timediff(@SystemTime) <= {lookbackMs}]]]";

                _logger.Info($"ModernDeployment backfill: scanning {ModernDeploymentManagementChannel} " +
                    $"(lookback={_modernDeploymentBackfillLookbackMinutes}min, targetedIds={TargetedManagementServiceEventIds.Count})");

                var query = new EventLogQuery(ModernDeploymentManagementChannel, PathType.LogName, xpath)
                {
                    ReverseDirection = true  // newest first — fire-once guard takes only the first match
                };

                int found = 0;
                using (var reader = new EventLogReader(query))
                {
                    EventRecord record;
                    while ((record = reader.ReadEvent()) != null)
                    {
                        using (record)
                        {
                            found++;
                            _logger.Info($"Backfill found ManagementService Event {record.Id} at {record.TimeCreated:O}");
                            ProcessModernDeploymentRecord(record, "ManagementService", ModernDeploymentManagementChannel, isBackfill: true);
                        }
                    }
                }

                if (found == 0)
                    _logger.Debug($"ModernDeployment backfill: no targeted events found in last {_modernDeploymentBackfillLookbackMinutes} minutes");
                else
                    _logger.Info($"ModernDeployment backfill: processed {found} event(s)");
            }
            catch (EventLogNotFoundException)
            {
                _logger.Warning($"ModernDeployment event log not found during backfill: {ModernDeploymentManagementChannel} (normal on non-Windows 10/11 test environments)");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.Warning($"ModernDeployment backfill access denied: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Warning($"ModernDeployment backfill failed: {ex.Message}");
            }
        }

        // -----------------------------------------------------------------------
        // Backfill state persistence (small JSON file for cross-restart dedup)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Persists the WhiteGlove detection flag to disk so the next agent start can skip
        /// the backfill scan. Uses atomic write (temp file + copy) to avoid partial writes.
        /// </summary>
        private void PersistWhiteGloveBackfillState(DateTime? eventTimeUtc)
        {
            if (string.IsNullOrEmpty(_stateDirectory))
                return;

            try
            {
                Directory.CreateDirectory(_stateDirectory);
                var filePath = Path.Combine(_stateDirectory, WhiteGloveBackfillStateFileName);
                var state = new WhiteGloveBackfillState
                {
                    WhiteGloveStartSeen = true,
                    SeenUtc = eventTimeUtc ?? DateTime.UtcNow,
                    PersistedUtc = DateTime.UtcNow
                };
                var json = JsonConvert.SerializeObject(state, Formatting.Indented);
                var tempPath = filePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Copy(tempPath, filePath, overwrite: true);
                try { File.Delete(tempPath); } catch { }
                _logger.Info($"WhiteGlove backfill state persisted to {filePath}");
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to persist WhiteGlove backfill state: {ex.Message}");
            }
        }

        /// <summary>
        /// Loads the persisted WhiteGlove backfill state from disk.
        /// Returns null if no state file exists or on error.
        /// </summary>
        private WhiteGloveBackfillState LoadWhiteGloveBackfillState()
        {
            if (string.IsNullOrEmpty(_stateDirectory))
                return null;

            var filePath = Path.Combine(_stateDirectory, WhiteGloveBackfillStateFileName);
            if (!File.Exists(filePath))
                return null;

            try
            {
                var json = File.ReadAllText(filePath);
                return JsonConvert.DeserializeObject<WhiteGloveBackfillState>(json);
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to load WhiteGlove backfill state: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Minimal DTO for the WhiteGlove backfill state file.
        /// Persisted as <c>whiteglove-backfill.json</c> in the agent state directory.
        /// </summary>
        private class WhiteGloveBackfillState
        {
            public bool WhiteGloveStartSeen { get; set; }
            public DateTime SeenUtc { get; set; }
            public DateTime PersistedUtc { get; set; }
        }
    }
}
