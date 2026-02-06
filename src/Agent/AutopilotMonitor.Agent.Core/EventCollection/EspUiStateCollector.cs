using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.Core.EventCollection
{
    /// <summary>
    /// Reads ESP (Enrollment Status Page) status from the registry
    /// Tracks blocking apps, install progress, and status text
    /// Optional collector - toggled on/off via remote config
    /// </summary>
    public class EspUiStateCollector : IDisposable
    {
        private readonly AgentLogger _logger;
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly Action<EnrollmentEvent> _onEventCollected;
        private readonly int _intervalSeconds;
        private Timer _pollTimer;

        // Track last known state to avoid duplicate events
        private string _lastStateHash;

        private const string EnrollmentsKeyPath = @"SOFTWARE\Microsoft\Enrollments";

        public EspUiStateCollector(string sessionId, string tenantId, Action<EnrollmentEvent> onEventCollected,
            AgentLogger logger, int intervalSeconds = 30)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _onEventCollected = onEventCollected ?? throw new ArgumentNullException(nameof(onEventCollected));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _intervalSeconds = intervalSeconds;
        }

        public void Start()
        {
            _logger.Info($"Starting ESP UI State collector (interval: {_intervalSeconds}s)");

            _pollTimer = new Timer(
                _ => CollectEspState(),
                null,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(_intervalSeconds)
            );
        }

        public void Stop()
        {
            _logger.Info("Stopping ESP UI State collector");
            _pollTimer?.Dispose();
            _pollTimer = null;
        }

        private void CollectEspState()
        {
            try
            {
                using (var enrollmentsKey = Registry.LocalMachine.OpenSubKey(EnrollmentsKeyPath, false))
                {
                    if (enrollmentsKey == null)
                        return;

                    var subKeyNames = enrollmentsKey.GetSubKeyNames();

                    foreach (var subKeyName in subKeyNames)
                    {
                        // Skip non-GUID keys
                        Guid guid;
                        if (!Guid.TryParse(subKeyName, out guid))
                            continue;

                        try
                        {
                            CollectEnrollmentEspState(enrollmentsKey, subKeyName);
                        }
                        catch (Exception ex)
                        {
                            _logger.Debug($"Failed to read ESP state for {subKeyName}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"ESP state collection failed: {ex.Message}");
            }
        }

        private void CollectEnrollmentEspState(RegistryKey enrollmentsKey, string enrollmentGuid)
        {
            using (var enrollmentKey = enrollmentsKey.OpenSubKey(enrollmentGuid, false))
            {
                if (enrollmentKey == null)
                    return;

                // Check for FirstSync tracking (ESP tracking policies)
                using (var firstSyncKey = enrollmentKey.OpenSubKey("FirstSync", false))
                {
                    if (firstSyncKey == null)
                        return;

                    var data = new Dictionary<string, object>();

                    // Read ESP status values
                    var deviceSetupStatus = ReadRegistryValue(firstSyncKey, "DeviceSetupStatus");
                    var accountSetupStatus = ReadRegistryValue(firstSyncKey, "AccountSetupStatus");
                    var espComplete = ReadRegistryValue(firstSyncKey, "IsComplete");

                    // Determine current ESP phase
                    string espPhase = "unknown";
                    if (accountSetupStatus != null)
                        espPhase = "account_setup";
                    else if (deviceSetupStatus != null)
                        espPhase = "device_setup";

                    data["phase"] = espPhase;
                    data["enrollment_guid"] = enrollmentGuid;

                    if (deviceSetupStatus != null)
                        data["device_setup_status"] = deviceSetupStatus;
                    if (accountSetupStatus != null)
                        data["account_setup_status"] = accountSetupStatus;
                    if (espComplete != null)
                        data["esp_complete"] = espComplete;

                    // Read blocking apps tracking
                    CollectBlockingApps(firstSyncKey, "Apps", data);

                    // Read tracking policies
                    CollectTrackingPolicies(firstSyncKey, data);

                    // Create state hash to avoid duplicate events
                    var stateHash = string.Join("|", data.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}"));
                    if (stateHash == _lastStateHash)
                        return; // No change

                    _lastStateHash = stateHash;

                    var blockingTotal = data.ContainsKey("blocking_apps_total") ? data["blocking_apps_total"] : 0;
                    var blockingCompleted = data.ContainsKey("blocking_apps_completed") ? data["blocking_apps_completed"] : 0;

                    _onEventCollected(new EnrollmentEvent
                    {
                        SessionId = _sessionId,
                        TenantId = _tenantId,
                        Timestamp = DateTime.UtcNow,
                        EventType = "esp_ui_state",
                        Severity = EventSeverity.Debug,
                        Source = "EspUiStateCollector",
                        Message = $"ESP State: {espPhase} - Apps: {blockingCompleted}/{blockingTotal}",
                        Data = data
                    });
                }
            }
        }

        private void CollectBlockingApps(RegistryKey firstSyncKey, string subKeyName, Dictionary<string, object> data)
        {
            try
            {
                using (var appsKey = firstSyncKey.OpenSubKey(subKeyName, false))
                {
                    if (appsKey == null)
                        return;

                    var appSubKeys = appsKey.GetSubKeyNames();
                    int total = appSubKeys.Length;
                    int completed = 0;
                    int failed = 0;
                    var appStatuses = new List<Dictionary<string, object>>();

                    foreach (var appId in appSubKeys)
                    {
                        using (var appKey = appsKey.OpenSubKey(appId, false))
                        {
                            if (appKey == null) continue;

                            var status = ReadRegistryValue(appKey, "Status");
                            var statusInt = 0;
                            if (status != null && int.TryParse(status, out statusInt))
                            {
                                if (statusInt == 1 || statusInt == 2) // Completed states
                                    completed++;
                                else if (statusInt == 3) // Failed
                                    failed++;
                            }

                            appStatuses.Add(new Dictionary<string, object>
                            {
                                { "app_id", appId },
                                { "status", status ?? "unknown" }
                            });
                        }
                    }

                    data["blocking_apps_total"] = total;
                    data["blocking_apps_completed"] = completed;
                    data["blocking_apps_failed"] = failed;

                    if (total > 0)
                    {
                        data["progress_percent"] = Math.Round(completed * 100.0 / total, 0);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"Failed to collect blocking apps: {ex.Message}");
            }
        }

        private void CollectTrackingPolicies(RegistryKey firstSyncKey, Dictionary<string, object> data)
        {
            try
            {
                using (var policiesKey = firstSyncKey.OpenSubKey("TrackingPoliciesCreated", false))
                {
                    if (policiesKey == null)
                        return;

                    var policyCount = policiesKey.GetSubKeyNames().Length;
                    data["tracking_policies_count"] = policyCount;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"Failed to collect tracking policies: {ex.Message}");
            }
        }

        private string ReadRegistryValue(RegistryKey key, string valueName)
        {
            try
            {
                var value = key.GetValue(valueName);
                return value?.ToString();
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
