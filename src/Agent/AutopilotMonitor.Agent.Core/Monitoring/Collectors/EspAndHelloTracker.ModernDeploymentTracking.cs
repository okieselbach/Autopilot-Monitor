using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.Core.Monitoring.Collectors
{
    /// <summary>
    /// Partial: ModernDeployment-Diagnostics-Provider event log handling (Ebene 1 — live capture).
    ///
    /// Subscribes to two Windows event channels that Microsoft uses to log Autopilot and ESP events:
    ///   - Microsoft-Windows-ModernDeployment-Diagnostics-Provider/Autopilot
    ///   - Microsoft-Windows-ModernDeployment-Diagnostics-Provider/ManagementService
    ///
    /// Current mode is **live-capture only**: every event at Level ≤ ModernDeploymentLogLevelMax
    /// (default 3 = Warning, Error, Critical) is forwarded to the backend as a
    /// <see cref="Constants.EventTypes.ModernDeploymentLog"/>/<see cref="Constants.EventTypes.ModernDeploymentWarning"/>/
    /// <see cref="Constants.EventTypes.ModernDeploymentError"/> event. We intentionally do NOT
    /// classify failures locally or fire <c>EspFailureDetected</c> from this code yet — we first
    /// want to gather real EventIDs from production devices and then iterate on classification rules
    /// via backend config without agent rollout.
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

        private void OnModernDeploymentEventRecordWritten(EventRecordWrittenEventArgs e, string shortName, string channelName)
        {
            if (e.EventRecord == null)
                return;

            try
            {
                var record = e.EventRecord;

                // Targeted event dispatch: WhiteGlove start (Event 509, Level 4).
                // Captured via the combined XPath filter — handle before the generic level switch.
                if (record.Id == EventId_ManagementService_WhiteGloveStart
                    && shortName == "ManagementService")
                {
                    HandleWhiteGloveStartEvent(record);
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
                    { "timeCreated", record.TimeCreated?.ToUniversalTime().ToString("o") ?? string.Empty }
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
            catch (Exception ex)
            {
                _logger.Error($"Error processing ModernDeployment event from {shortName}", ex);
            }
        }

        /// <summary>
        /// Handles ManagementService Event 509 (WhiteGlove start indicator).
        /// Called from the shared OnModernDeploymentEventRecordWritten handler when a targeted
        /// event ID matches. Emits a whiteglove_started event for monitoring — no actions derived.
        /// </summary>
        private void HandleWhiteGloveStartEvent(EventRecord record)
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

            _logger.Info($"WhiteGlove start detected via ManagementService Event 509: {description}");

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
                    { "timeCreated", record.TimeCreated?.ToUniversalTime().ToString("o") ?? string.Empty }
                },
                ImmediateUpload = true
            });
        }
    }
}
