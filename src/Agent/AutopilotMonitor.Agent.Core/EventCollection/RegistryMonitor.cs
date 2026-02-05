using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.Core.EventCollection
{
    /// <summary>
    /// Monitors Autopilot registry keys for status changes
    /// </summary>
    public class RegistryMonitor : IDisposable
    {
        private readonly AgentLogger _logger;
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly Action<EnrollmentEvent> _onEventCollected;
        private Timer _pollTimer;
        private readonly Dictionary<string, object> _lastValues = new Dictionary<string, object>();

        // Autopilot registry paths
        private const string AutopilotKeyPath = @"SOFTWARE\Microsoft\Provisioning\Diagnostics\Autopilot";
        private const string AutopilotDiagnosticsPath = @"SOFTWARE\Microsoft\Provisioning\Diagnostics";

        // Registry keys to monitor
        private static readonly string[] MonitoredValues = new[]
        {
            "CloudAssignedTenantId",
            "CloudAssignedOobeConfig",
            "AutopilotServiceCorrelationId",
            "DeploymentProfileName",
            "IsAutopilotDisabled",
            "CloudAssignedTenantDomain"
        };

        public RegistryMonitor(string sessionId, string tenantId, Action<EnrollmentEvent> onEventCollected, AgentLogger logger)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _onEventCollected = onEventCollected ?? throw new ArgumentNullException(nameof(onEventCollected));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Starts monitoring registry keys
        /// </summary>
        public void Start()
        {
            _logger.Info("Starting Registry monitor");

            // Initial read
            CheckRegistryValues();

            // Poll every 5 seconds
            _pollTimer = new Timer(
                _ => CheckRegistryValues(),
                null,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5)
            );
        }

        /// <summary>
        /// Stops monitoring registry keys
        /// </summary>
        public void Stop()
        {
            _logger.Info("Stopping Registry monitor");
            _pollTimer?.Dispose();
        }

        private void CheckRegistryValues()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(AutopilotKeyPath, false))
                {
                    if (key == null)
                    {
                        // Autopilot key doesn't exist - not an Autopilot device or not started yet
                        return;
                    }

                    foreach (var valueName in MonitoredValues)
                    {
                        try
                        {
                            var currentValue = key.GetValue(valueName);

                            if (currentValue != null)
                            {
                                var valueString = currentValue.ToString();

                                // Check if value changed
                                if (!_lastValues.ContainsKey(valueName) || !_lastValues[valueName].Equals(currentValue))
                                {
                                    _lastValues[valueName] = currentValue;

                                    // Emit event for registry change
                                    var evt = new EnrollmentEvent
                                    {
                                        SessionId = _sessionId,
                                        TenantId = _tenantId,
                                        Timestamp = DateTime.UtcNow,
                                        EventType = $"registry_changed",
                                        Severity = EventSeverity.Info,
                                        Source = "RegistryMonitor",
                                        Phase = DetectPhaseFromKey(valueName),
                                        Message = $"Registry value changed: {valueName} = {valueString}",
                                        Data = new Dictionary<string, object>
                                        {
                                            { "registryKey", AutopilotKeyPath },
                                            { "valueName", valueName },
                                            { "value", valueString }
                                        }
                                    };

                                    _onEventCollected(evt);
                                    _logger.Debug($"Registry change detected: {valueName} = {valueString}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Error($"Error reading registry value {valueName}", ex);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Error checking registry values", ex);
            }
        }

        private EnrollmentPhase DetectPhaseFromKey(string keyName)
        {
            // Map registry keys to phases
            if (keyName.Contains("CloudAssigned") || keyName.Contains("Tenant"))
                return EnrollmentPhase.Identity;
            if (keyName.Contains("Profile") || keyName.Contains("Config"))
                return EnrollmentPhase.MdmEnrollment;

            return EnrollmentPhase.PreFlight;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
