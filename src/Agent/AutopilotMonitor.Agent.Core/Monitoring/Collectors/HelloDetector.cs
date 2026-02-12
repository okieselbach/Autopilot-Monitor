using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.Core.Monitoring.Collectors
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
        private System.Threading.Timer _policyCheckTimer;

        private bool _isPolicyConfigured = false;
        private bool _isHelloCompleted = false;
        private readonly object _stateLock = new object();

        /// <summary>
        /// Callback invoked when Hello provisioning completes (successfully, failed, skipped, or blocked)
        /// </summary>
        public event EventHandler HelloCompleted;

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

        /// <summary>
        /// Gets whether Windows Hello for Business policy is configured (enabled or disabled)
        /// </summary>
        public bool IsPolicyConfigured
        {
            get { lock (_stateLock) { return _isPolicyConfigured; } }
        }

        /// <summary>
        /// Gets whether Windows Hello provisioning has completed (successfully, failed, or skipped)
        /// </summary>
        public bool IsHelloCompleted
        {
            get { lock (_stateLock) { return _isHelloCompleted; } }
        }

        public void Start()
        {
            _logger.Info("Starting Hello detector");

            // Check if WHfB policy is configured initially
            CheckHelloPolicy();

            // Start periodic policy check (every 30 seconds) to detect policy arriving later via MDM
            _policyCheckTimer = new System.Threading.Timer(
                _ => CheckHelloPolicy(),
                null,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(30)
            );

            // Subscribe to User Device Registration event log
            StartEventLogWatcher();
        }

        public void Stop()
        {
            _logger.Info("Stopping Hello detector");

            // Stop periodic policy check timer
            if (_policyCheckTimer != null)
            {
                try
                {
                    _policyCheckTimer.Dispose();
                    _policyCheckTimer = null;
                }
                catch (Exception ex)
                {
                    _logger.Error("Error stopping Hello detector policy check timer", ex);
                }
            }

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

                lock (_stateLock)
                {
                    // If policy was not configured before but is now, update state and emit event
                    if (!_isPolicyConfigured && isEnabled.HasValue)
                    {
                        _isPolicyConfigured = true;
                        var status = isEnabled.Value ? "enabled" : "disabled";

                        _onEventCollected(new EnrollmentEvent
                        {
                            SessionId = _sessionId,
                            TenantId = _tenantId,
                            EventType = "hello_policy_detected",
                            Severity = EventSeverity.Info,
                            Source = "HelloDetector",
                            Phase = EnrollmentPhase.Unknown,
                            Message = $"Windows Hello for Business policy detected: {status} (via {source})",
                            Data = new Dictionary<string, object>
                            {
                                { "helloEnabled", isEnabled.Value },
                                { "policySource", source }
                            }
                        });

                        _logger.Info($"WHfB policy detected: {status} (source: {source})");

                        // Stop periodic policy check - we found what we were looking for
                        if (_policyCheckTimer != null)
                        {
                            _policyCheckTimer.Dispose();
                            _policyCheckTimer = null;
                            _logger.Debug("Stopped periodic Hello policy check - policy has been detected");
                        }
                    }
                    else if (!_isPolicyConfigured && !isEnabled.HasValue)
                    {
                        // Policy still not found - this is normal during early enrollment (Debug level to keep UI clean)
                        _logger.Debug("Periodic Hello policy check: No WHfB policy found yet - will check again");
                    }
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
            // Policy can be device-scoped: {tenantId}\Device\Policies
            // or user-scoped: {tenantId}\{userSid}\Policies
            try
            {
                using (var baseCspKey = Registry.LocalMachine.OpenSubKey(CspPolicyBasePath, false))
                {
                    if (baseCspKey != null)
                    {
                        foreach (var tenantSubKey in baseCspKey.GetSubKeyNames())
                        {
                            using (var tenantKey = baseCspKey.OpenSubKey(tenantSubKey, false))
                            {
                                if (tenantKey != null)
                                {
                                    // Check all subkeys (Device or user SIDs like S-1-5-...)
                                    foreach (var scopeSubKey in tenantKey.GetSubKeyNames())
                                    {
                                        using (var policiesKey = tenantKey.OpenSubKey($@"{scopeSubKey}\Policies", false))
                                        {
                                            if (policiesKey != null)
                                            {
                                                var value = policiesKey.GetValue("UsePassportForWork");
                                                if (value != null)
                                                {
                                                    var scope = scopeSubKey.Equals("Device", StringComparison.OrdinalIgnoreCase)
                                                        ? "device"
                                                        : "user";
                                                    return (Convert.ToInt32(value) == 1, $"CSP/Intune ({scope}-scoped)");
                                                }
                                            }
                                        }
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
                        // Mark Hello as completed (terminal event)
                        lock (_stateLock) { _isHelloCompleted = true; }
                        _logger.Info("Windows Hello provisioning COMPLETED successfully");
                        // Trigger HelloCompleted event for EnrollmentTracker
                        try { HelloCompleted?.Invoke(this, EventArgs.Empty); } catch { }
                        break;

                    case EventId_NgcKeyRegistrationFailed: // 301
                        eventType = "hello_provisioning_failed";
                        severity = EventSeverity.Error;
                        message = "Windows Hello for Business provisioning failed - NGC key registration error";
                        // Mark Hello as completed (terminal event - failed counts as completed)
                        lock (_stateLock) { _isHelloCompleted = true; }
                        _logger.Info("Windows Hello provisioning COMPLETED with failure");
                        // Trigger HelloCompleted event for EnrollmentTracker
                        try { HelloCompleted?.Invoke(this, EventArgs.Empty); } catch { }
                        break;

                    case EventId_ProvisioningWillNotLaunch: // 360
                        eventType = "hello_provisioning_skipped";
                        severity = EventSeverity.Warning;
                        message = "Windows Hello for Business provisioning skipped - prerequisites not met";
                        // Mark Hello as completed (terminal event - skipped counts as completed)
                        lock (_stateLock) { _isHelloCompleted = true; }
                        _logger.Info("Windows Hello provisioning COMPLETED (skipped)");
                        // Trigger HelloCompleted event for EnrollmentTracker
                        try { HelloCompleted?.Invoke(this, EventArgs.Empty); } catch { }
                        break;

                    case EventId_ProvisioningBlocked: // 362
                        eventType = "hello_provisioning_blocked";
                        severity = EventSeverity.Warning;
                        message = "Windows Hello for Business provisioning blocked";
                        // Mark Hello as completed (terminal event - blocked counts as completed)
                        lock (_stateLock) { _isHelloCompleted = true; }
                        _logger.Info("Windows Hello provisioning COMPLETED (blocked)");
                        // Trigger HelloCompleted event for EnrollmentTracker
                        try { HelloCompleted?.Invoke(this, EventArgs.Empty); } catch { }
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
                    Phase = EnrollmentPhase.Unknown,
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
