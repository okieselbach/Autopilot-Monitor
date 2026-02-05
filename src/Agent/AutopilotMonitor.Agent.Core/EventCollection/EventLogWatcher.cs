using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.Core.EventCollection
{
    /// <summary>
    /// Watches Windows Event Logs for Autopilot-related events
    /// </summary>
    public class EventLogWatcher : IDisposable
    {
        private readonly AgentLogger _logger;
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly Action<EnrollmentEvent> _onEventCollected;
        private readonly List<EventLogQuery> _queries = new List<EventLogQuery>();
        private readonly List<System.Diagnostics.Eventing.Reader.EventLogWatcher> _watchers = new List<System.Diagnostics.Eventing.Reader.EventLogWatcher>();

        // Autopilot Event Log channels
        private static readonly string[] AutopilotLogChannels = new[]
        {
            "Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider/Admin",
            "Microsoft-Windows-Provisioning-Diagnostics-Provider/Admin",
            "Microsoft-Windows-ModernDeployment-Diagnostics-Provider/Autopilot"
        };

        public EventLogWatcher(string sessionId, string tenantId, Action<EnrollmentEvent> onEventCollected, AgentLogger logger)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _onEventCollected = onEventCollected ?? throw new ArgumentNullException(nameof(onEventCollected));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Starts watching event logs
        /// </summary>
        public void Start()
        {
            _logger.Info("Starting EventLog watchers");

            foreach (var channel in AutopilotLogChannels)
            {
                try
                {
                    // Create query for new events (from now onwards)
                    var query = new EventLogQuery(channel, PathType.LogName, "*[System[TimeCreated[timediff(@SystemTime) <= 1000]]]");
                    _queries.Add(query);

                    // Create watcher
                    var watcher = new System.Diagnostics.Eventing.Reader.EventLogWatcher(query);
                    watcher.EventRecordWritten += OnEventRecordWritten;
                    watcher.Enabled = true;

                    _watchers.Add(watcher);
                    _logger.Info($"Started watching: {channel}");
                }
                catch (EventLogNotFoundException)
                {
                    _logger.Warning($"Event log not found: {channel} (This is normal if not on a real Autopilot device)");
                }
                catch (Exception ex)
                {
                    _logger.Error($"Failed to start watcher for {channel}", ex);
                }
            }

            if (_watchers.Count == 0)
            {
                _logger.Warning("No event log watchers started. Running in simulation mode.");
            }
        }

        /// <summary>
        /// Stops watching event logs
        /// </summary>
        public void Stop()
        {
            _logger.Info("Stopping EventLog watchers");

            foreach (var watcher in _watchers)
            {
                try
                {
                    watcher.Enabled = false;
                    watcher.EventRecordWritten -= OnEventRecordWritten;
                    watcher.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.Error("Error stopping watcher", ex);
                }
            }

            _watchers.Clear();
            _queries.Clear();
        }

        private void OnEventRecordWritten(object sender, EventRecordWrittenEventArgs e)
        {
            if (e.EventRecord == null)
                return;

            try
            {
                var record = e.EventRecord;

                // Convert Windows Event to EnrollmentEvent
                var evt = new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId = _tenantId,
                    Timestamp = record.TimeCreated ?? DateTime.UtcNow,
                    EventType = $"eventlog_{record.Id}",
                    Severity = MapEventLevel(record.Level),
                    Source = record.ProviderName ?? "EventLog",
                    Phase = DetectPhaseFromEvent(record),
                    Message = record.FormatDescription() ?? $"Event ID {record.Id}",
                    Data = new Dictionary<string, object>
                    {
                        { "eventId", record.Id },
                        { "level", record.Level ?? 0 },
                        { "logName", record.LogName },
                        { "providerName", record.ProviderName }
                    }
                };

                _onEventCollected(evt);
            }
            catch (Exception ex)
            {
                _logger.Error("Error processing event record", ex);
            }
        }

        private EventSeverity MapEventLevel(byte? level)
        {
            if (!level.HasValue)
                return EventSeverity.Info;

            switch (level.Value)
            {
                case 1: // Critical
                    return EventSeverity.Critical;
                case 2: // Error
                    return EventSeverity.Error;
                case 3: // Warning
                    return EventSeverity.Warning;
                case 4: // Information
                default:
                    return EventSeverity.Info;
            }
        }

        private EnrollmentPhase DetectPhaseFromEvent(EventRecord record)
        {
            // Simple phase detection based on event content
            var description = record.FormatDescription()?.ToLowerInvariant() ?? "";

            if (description.Contains("device preparation") || description.Contains("preparing device"))
                return EnrollmentPhase.MdmEnrollment;
            if (description.Contains("device setup") || description.Contains("setting up device"))
                return EnrollmentPhase.EspDeviceSetup;
            if (description.Contains("account setup") || description.Contains("setting up account") || description.Contains("user setup"))
                return EnrollmentPhase.EspUserSetup;
            if (description.Contains("esp") || description.Contains("enrollment status"))
                return EnrollmentPhase.EspDeviceSetup;
            if (description.Contains("identity") || description.Contains("azure ad") || description.Contains("entra"))
                return EnrollmentPhase.Identity;
            if (description.Contains("app") || description.Contains("application"))
                return EnrollmentPhase.AppInstallation;
            if (description.Contains("complete") || description.Contains("finished"))
                return EnrollmentPhase.Complete;

            return EnrollmentPhase.PreFlight;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
