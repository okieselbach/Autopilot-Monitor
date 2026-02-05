using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.Core.EventCollection
{
    /// <summary>
    /// Detects and tracks Windows Hello for Business provisioning during Autopilot enrollment.
    /// Monitors the "Microsoft-Windows-User Device Registration/Admin" event log channel
    /// and checks registry for WHfB policy configuration.
    ///
    /// Key Event IDs:
    ///   358 - Prerequisites passed, provisioning will be launched
    ///   360 - Prerequisites failed, provisioning will NOT be launched
    ///   362 - Provisioning blocked
    ///   300 - NGC key registered successfully (Hello provisioned)
    ///   301 - NGC key registration failed
    /// </summary>
    public class HelloDetector : IDisposable
    {
        private readonly AgentLogger _logger;
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly Action<EnrollmentEvent> _onEventCollected;
        private System.Diagnostics.Eventing.Reader.EventLogWatcher _watcher;

        private const string EventLogChannel = "Microsoft-Windows-User Device Registration/Admin";

        // WHfB policy registry paths
        private const string CspPolicyBasePath = @"SOFTWARE\Microsoft\Policies\PassportForWork";
        private const string GpoPolicyPath = @"SOFTWARE\Policies\Microsoft\PassportForWork";

        // Tracked event IDs
        private const int EventId_NgcKeyRegistered = 300;
        private const int EventId_NgcKeyRegistrationFailed = 301;
        private const int EventId_ProvisioningWillLaunch = 358;
        private const int EventId_ProvisioningWillNotLaunch = 360;
        private const int EventId_ProvisioningBlocked = 362;
        private const int EventId_PinStatus = 376;

        private static readonly HashSet<int> TrackedEventIds = new HashSet<int>
        {
            EventId_NgcKeyRegistered,
            EventId_NgcKeyRegistrationFailed,
            EventId_ProvisioningWillLaunch,
            EventId_ProvisioningWillNotLaunch,
            EventId_ProvisioningBlocked,
            EventId_PinStatus
        };

        public HelloDetector(string sessionId, string tenantId, Action<EnrollmentEvent> onEventCollected, AgentLogger logger)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _onEventCollected = onEventCollected ?? throw new ArgumentNullException(nameof(onEventCollected));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void Start()
        {
            _logger.Info("Starting Hello detector");

            // Check if WHfB policy is configured
            CheckHelloPolicy();

            // Subscribe to User Device Registration event log
            StartEventLogWatcher();
        }

        public void Stop()
        {
            _logger.Info("Stopping Hello detector");

            if (_watcher != null)
            {
                try
                {
                    _watcher.Enabled = false;
                    _watcher.EventRecordWritten -= OnEventRecordWritten;
                    _watcher.Dispose();
                    _watcher = null;
                }
                catch (Exception ex)
                {
                    _logger.Error("Error stopping Hello detector watcher", ex);
                }
            }
        }

        private void CheckHelloPolicy()
        {
            try
            {
                var (isEnabled, source) = DetectHelloPolicy();

                if (isEnabled.HasValue)
                {
                    var status = isEnabled.Value ? "enabled" : "disabled";

                    _onEventCollected(new EnrollmentEvent
                    {
                        SessionId = _sessionId,
                        TenantId = _tenantId,
                        EventType = "hello_policy_detected",
                        Severity = EventSeverity.Info,
                        Source = "HelloDetector",
                        Phase = EnrollmentPhase.EspDeviceSetup,
                        Message = $"Windows Hello for Business policy detected: {status} (via {source})",
                        Data = new Dictionary<string, object>
                        {
                            { "helloEnabled", isEnabled.Value },
                            { "policySource", source }
                        }
                    });

                    _logger.Info($"WHfB policy: {status} (source: {source})");
                }
                else
                {
                    _logger.Info("No WHfB policy found in registry - Hello may use tenant default");
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Error checking WHfB policy: {ex.Message}");
            }
        }

        private (bool? isEnabled, string source) DetectHelloPolicy()
        {
            // 1. Check CSP/Intune-delivered policy (per-tenant paths)
            try
            {
                using (var baseCspKey = Registry.LocalMachine.OpenSubKey(CspPolicyBasePath, false))
                {
                    if (baseCspKey != null)
                    {
                        foreach (var tenantSubKey in baseCspKey.GetSubKeyNames())
                        {
                            using (var policiesKey = baseCspKey.OpenSubKey($@"{tenantSubKey}\Device\Policies", false))
                            {
                                if (policiesKey != null)
                                {
                                    var value = policiesKey.GetValue("UsePassportForWork");
                                    if (value != null)
                                    {
                                        return (Convert.ToInt32(value) == 1, "CSP/Intune");
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            // 2. Check GPO-delivered policy
            try
            {
                using (var gpoKey = Registry.LocalMachine.OpenSubKey(GpoPolicyPath, false))
                {
                    if (gpoKey != null)
                    {
                        var value = gpoKey.GetValue("Enabled");
                        if (value != null)
                        {
                            return (Convert.ToInt32(value) == 1, "GPO");
                        }
                    }
                }
            }
            catch { }

            return (null, null);
        }

        private void StartEventLogWatcher()
        {
            try
            {
                // Watch for specific WHfB-related event IDs
                var query = new EventLogQuery(
                    EventLogChannel,
                    PathType.LogName,
                    "*[System[TimeCreated[timediff(@SystemTime) <= 1000]]]"
                );

                _watcher = new System.Diagnostics.Eventing.Reader.EventLogWatcher(query);
                _watcher.EventRecordWritten += OnEventRecordWritten;
                _watcher.Enabled = true;

                _logger.Info($"Started watching: {EventLogChannel}");
            }
            catch (EventLogNotFoundException)
            {
                _logger.Warning($"Event log not found: {EventLogChannel} (normal if not on a real device)");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to start Hello event log watcher", ex);
            }
        }

        private void OnEventRecordWritten(object sender, EventRecordWrittenEventArgs e)
        {
            if (e.EventRecord == null)
                return;

            try
            {
                var record = e.EventRecord;
                var eventId = record.Id;

                if (!TrackedEventIds.Contains(eventId))
                    return;

                var description = record.FormatDescription() ?? $"Event ID {eventId}";

                string eventType;
                EventSeverity severity;
                string message;

                switch (eventId)
                {
                    case EventId_ProvisioningWillLaunch: // 358
                        eventType = "hello_provisioning_started";
                        severity = EventSeverity.Info;
                        message = "Windows Hello for Business provisioning started - prerequisites passed";
                        break;

                    case EventId_NgcKeyRegistered: // 300
                        eventType = "hello_provisioning_completed";
                        severity = EventSeverity.Info;
                        message = "Windows Hello for Business provisioned successfully - NGC key registered";
                        break;

                    case EventId_NgcKeyRegistrationFailed: // 301
                        eventType = "hello_provisioning_failed";
                        severity = EventSeverity.Error;
                        message = "Windows Hello for Business provisioning failed - NGC key registration error";
                        break;

                    case EventId_ProvisioningWillNotLaunch: // 360
                        eventType = "hello_provisioning_skipped";
                        severity = EventSeverity.Warning;
                        message = "Windows Hello for Business provisioning skipped - prerequisites not met";
                        break;

                    case EventId_ProvisioningBlocked: // 362
                        eventType = "hello_provisioning_blocked";
                        severity = EventSeverity.Warning;
                        message = "Windows Hello for Business provisioning blocked";
                        break;

                    case EventId_PinStatus: // 376
                        eventType = "hello_pin_status";
                        severity = EventSeverity.Info;
                        message = "Windows Hello PIN status update";
                        break;

                    default:
                        return;
                }

                _onEventCollected(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId = _tenantId,
                    Timestamp = record.TimeCreated ?? DateTime.UtcNow,
                    EventType = eventType,
                    Severity = severity,
                    Source = "HelloDetector",
                    Phase = EnrollmentPhase.EspDeviceSetup,
                    Message = message,
                    Data = new Dictionary<string, object>
                    {
                        { "windowsEventId", eventId },
                        { "providerName", record.ProviderName ?? "" },
                        { "description", description }
                    }
                });

                _logger.Info($"Hello event detected: {eventType} (EventID {eventId})");
            }
            catch (Exception ex)
            {
                _logger.Error("Error processing Hello event record", ex);
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
