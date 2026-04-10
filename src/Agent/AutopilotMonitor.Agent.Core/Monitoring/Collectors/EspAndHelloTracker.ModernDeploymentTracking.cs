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

        private System.Diagnostics.Eventing.Reader.EventLogWatcher _modernDeploymentAutopilotWatcher;
        private System.Diagnostics.Eventing.Reader.EventLogWatcher _modernDeploymentManagementWatcher;

        private void StartModernDeploymentEventLogWatchers()
        {
            _modernDeploymentAutopilotWatcher = TryStartModernDeploymentWatcher(ModernDeploymentAutopilotChannel, "Autopilot");
            _modernDeploymentManagementWatcher = TryStartModernDeploymentWatcher(ModernDeploymentManagementChannel, "ManagementService");
        }

        private System.Diagnostics.Eventing.Reader.EventLogWatcher TryStartModernDeploymentWatcher(string channelName, string shortName)
        {
            try
            {
                // Clamp the level filter into the valid Windows range [1..5].
                var levelMax = Math.Max(1, Math.Min(5, _modernDeploymentLogLevelMax));

                // XPath: (Level >= 1 and Level <= levelMax). Level 0 ("LogAlways") is not filtered out by
                // XPath comparison — Windows treats it separately — so we explicitly include it in the query.
                var xpath = $"*[System[Level=0 or (Level >= 1 and Level <= {levelMax})]]";

                var query = new EventLogQuery(channelName, PathType.LogName, xpath);
                var watcher = new System.Diagnostics.Eventing.Reader.EventLogWatcher(query);
                watcher.EventRecordWritten += (sender, args) => OnModernDeploymentEventRecordWritten(args, shortName, channelName);
                watcher.Enabled = true;

                _logger.Info($"ModernDeployment watcher started: {channelName} (levelMax={levelMax})");
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
    }
}
