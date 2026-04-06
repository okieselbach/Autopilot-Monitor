using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.Core.Monitoring.Collectors
{
    /// <summary>
    /// Partial: HelloForBusiness/Operational event log handling — Hello processing
    /// started/stopped signals and skip detection via event 6045.
    /// </summary>
    public partial class EspAndHelloTracker
    {
        private static readonly Regex HResultPattern = new Regex(@"0x[0-9A-Fa-f]{8}", RegexOptions.Compiled);

        private void StartHelloForBusinessEventLogWatcher()
        {
            try
            {
                var query = new EventLogQuery(
                    HelloForBusinessEventLogChannel,
                    PathType.LogName,
                    "*[System[(EventID=3024 or EventID=6045)]]"
                );

                _helloForBusinessWatcher = new System.Diagnostics.Eventing.Reader.EventLogWatcher(query);
                _helloForBusinessWatcher.EventRecordWritten += OnHelloForBusinessEventRecordWritten;
                _helloForBusinessWatcher.Enabled = true;

                _logger.Info($"Started watching: {HelloForBusinessEventLogChannel}");
            }
            catch (EventLogNotFoundException)
            {
                _logger.Warning($"Event log not found: {HelloForBusinessEventLogChannel} (normal if not on a real device)");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to start HelloForBusiness event log watcher", ex);
            }
        }

        private void OnHelloForBusinessEventRecordWritten(object sender, EventRecordWrittenEventArgs e)
        {
            if (e.EventRecord == null)
                return;

            try
            {
                var record = e.EventRecord;
                var eventId = record.Id;
                if (!TrackedHelloForBusinessEventIds.Contains(eventId))
                    return;

                var description = record.FormatDescription() ?? $"Event ID {eventId}";
                var timestamp = (record.TimeCreated ?? DateTime.UtcNow).ToUniversalTime();

                ProcessHelloForBusinessEvent(
                    eventId,
                    timestamp,
                    description,
                    record.ProviderName ?? "",
                    isBackfill: false);
            }
            catch (Exception ex)
            {
                _logger.Error("Error processing HelloForBusiness event record", ex);
            }
        }

        private void ProcessHelloForBusinessEvent(int eventId, DateTime timestamp, string description, string providerName, bool isBackfill)
        {
            string eventType;
            EventSeverity severity;
            string message;
            bool shouldTriggerHelloCompleted = false;
            string hresult = null;

            switch (eventId)
            {
                case EventId_HelloForBusiness_ProcessingStarted: // 3024
                    eventType = "hello_processing_started";
                    severity = EventSeverity.Info;
                    message = "Windows Hello for Business processing started";
                    _logger.Info("Hello for Business processing started (event 3024)");
                    break;

                case EventId_HelloForBusiness_ProcessingStopped: // 6045
                    hresult = ExtractHResultFromDescription(description);

                    if (string.Equals(hresult, HResult_UserSkippedHello, StringComparison.OrdinalIgnoreCase))
                    {
                        eventType = "hello_skipped";
                        severity = EventSeverity.Warning;
                        message = $"Windows Hello for Business skipped by user ({HResult_UserSkippedHello})";
                        shouldTriggerHelloCompleted = MarkHelloSkipped();
                        _logger.Info($"Hello for Business SKIPPED by user (event 6045, HRESULT {hresult})");
                    }
                    else
                    {
                        eventType = "hello_processing_stopped";
                        severity = EventSeverity.Info;
                        message = $"Windows Hello for Business processing stopped (HRESULT: {hresult ?? "unknown"})";
                        _logger.Info($"Hello for Business processing stopped (event 6045, HRESULT {hresult ?? "unknown"}) - not treated as terminal");
                    }
                    break;

                default:
                    return;
            }

            var data = new Dictionary<string, object>
            {
                { "windowsEventId", eventId },
                { "providerName", providerName ?? "" },
                { "description", description },
                { "eventLogChannel", HelloForBusinessEventLogChannel },
                { "eventTime", timestamp.ToString("o") }
            };

            if (hresult != null)
            {
                data["hresult"] = hresult;
            }

            if (isBackfill)
            {
                data["backfill"] = true;
            }

            _onEventCollected(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                Timestamp = timestamp,
                EventType = eventType,
                Severity = severity,
                Source = "EspAndHelloTracker",
                Phase = EnrollmentPhase.Unknown,
                Message = message,
                Data = data,
                ImmediateUpload = shouldTriggerHelloCompleted
            });

            _logger.Info($"HelloForBusiness event detected: {eventType} (EventID {eventId}{(isBackfill ? ", backfill" : "")})");

            if (shouldTriggerHelloCompleted)
            {
                try { HelloCompleted?.Invoke(this, EventArgs.Empty); } catch { }
            }
        }

        private bool MarkHelloSkipped()
        {
            lock (_stateLock)
            {
                if (_isHelloCompleted)
                {
                    _logger.Debug("Hello skip event received but Hello is already marked completed");
                    return false;
                }

                _isHelloCompleted = true;
                HelloOutcome = "skipped";
                StopHelloCompletionTimerLocked();
                return true;
            }
        }

        private static string ExtractHResultFromDescription(string description)
        {
            if (string.IsNullOrEmpty(description))
                return null;

            var match = HResultPattern.Match(description);
            return match.Success ? match.Value : null;
        }

        private void BackfillRecentHelloForBusinessEvents()
        {
            try
            {
                var lookbackMs = BackfillLookbackMinutes * 60 * 1000;
                var query = new EventLogQuery(
                    HelloForBusinessEventLogChannel,
                    PathType.LogName,
                    $"*[System[(EventID=6045) and TimeCreated[timediff(@SystemTime) <= {lookbackMs}]]]");

                using (var reader = new EventLogReader(query))
                {
                    DateTime latestTimestamp = DateTime.MinValue;
                    string latestDescription = null;
                    string latestProvider = string.Empty;

                    for (EventRecord record = reader.ReadEvent(); record != null; record = reader.ReadEvent())
                    {
                        using (record)
                        {
                            var timestamp = record.TimeCreated ?? DateTime.MinValue;
                            if (timestamp < latestTimestamp)
                                continue;

                            latestTimestamp = timestamp;
                            latestDescription = record.FormatDescription() ?? $"Event ID {record.Id}";
                            latestProvider = record.ProviderName ?? "";
                        }
                    }

                    if (latestDescription != null)
                    {
                        var hresult = ExtractHResultFromDescription(latestDescription);
                        // Only backfill if it's a known terminal HRESULT (skip)
                        if (string.Equals(hresult, HResult_UserSkippedHello, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.Info($"Backfill found recent Hello skip event: HRESULT {hresult} at {latestTimestamp:O}");
                            ProcessHelloForBusinessEvent(
                                EventId_HelloForBusiness_ProcessingStopped,
                                (latestTimestamp == DateTime.MinValue ? DateTime.UtcNow : latestTimestamp).ToUniversalTime(),
                                latestDescription,
                                latestProvider,
                                isBackfill: true);
                        }
                        else
                        {
                            _logger.Debug($"Backfill found HelloForBusiness event 6045 but HRESULT {hresult ?? "unknown"} is not a known terminal code - skipping");
                        }
                    }
                    else
                    {
                        _logger.Debug($"Backfill found no HelloForBusiness 6045 events in last {BackfillLookbackMinutes} minutes");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"HelloForBusiness event backfill failed: {ex.Message}");
            }
        }
    }
}
