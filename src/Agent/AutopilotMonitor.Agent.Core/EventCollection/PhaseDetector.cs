using System;
using System.Collections.Generic;
using System.Threading;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.Core.EventCollection
{
    /// <summary>
    /// Detects and tracks Autopilot enrollment phases
    /// </summary>
    public class PhaseDetector : IDisposable
    {
        private readonly AgentLogger _logger;
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly Action<EnrollmentEvent> _onEventCollected;
        private Timer _pollTimer;
        private EnrollmentPhase _currentPhase = EnrollmentPhase.PreFlight;

        // ESP (Enrollment Status Page) registry paths
        private const string EspKeyPath = @"SOFTWARE\Microsoft\Enrollments";
        private const string AutopilotKeyPath = @"SOFTWARE\Microsoft\Provisioning\Diagnostics\Autopilot";

        public EnrollmentPhase CurrentPhase => _currentPhase;

        public PhaseDetector(string sessionId, string tenantId, Action<EnrollmentEvent> onEventCollected, AgentLogger logger)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _onEventCollected = onEventCollected ?? throw new ArgumentNullException(nameof(onEventCollected));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Starts phase detection
        /// </summary>
        public void Start()
        {
            _logger.Info("Starting Phase detector");

            // Initial phase check
            CheckPhase();

            // Poll every 10 seconds
            _pollTimer = new Timer(
                _ => CheckPhase(),
                null,
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(10)
            );
        }

        /// <summary>
        /// Stops phase detection
        /// </summary>
        public void Stop()
        {
            _logger.Info("Stopping Phase detector");
            _pollTimer?.Dispose();
        }

        private void CheckPhase()
        {
            try
            {
                var detectedPhase = DetectCurrentPhase();

                if (detectedPhase != _currentPhase)
                {
                    var previousPhase = _currentPhase;
                    _currentPhase = detectedPhase;

                    // Emit phase change event
                    var evt = new EnrollmentEvent
                    {
                        SessionId = _sessionId,
                        TenantId = _tenantId,
                        Timestamp = DateTime.UtcNow,
                        EventType = "phase_changed",
                        Severity = EventSeverity.Info,
                        Source = "PhaseDetector",
                        Phase = _currentPhase,
                        Message = $"Enrollment phase changed: {previousPhase} -> {_currentPhase}",
                        Data = new Dictionary<string, object>
                        {
                            { "previousPhase", previousPhase.ToString() },
                            { "currentPhase", _currentPhase.ToString() },
                            { "phaseNumber", (int)_currentPhase }
                        }
                    };

                    _onEventCollected(evt);
                    _logger.Info($"Phase changed: {previousPhase} -> {_currentPhase}");

                    // Check if enrollment completed or failed
                    if (_currentPhase == EnrollmentPhase.Complete)
                    {
                        _logger.Info("Enrollment completed successfully - emitting completion event");
                        var completionEvent = new EnrollmentEvent
                        {
                            SessionId = _sessionId,
                            TenantId = _tenantId,
                            Timestamp = DateTime.UtcNow,
                            EventType = "enrollment_complete",
                            Severity = EventSeverity.Info,
                            Source = "PhaseDetector",
                            Phase = EnrollmentPhase.Complete,
                            Message = "Autopilot enrollment completed successfully"
                        };
                        _onEventCollected(completionEvent);
                    }
                    else if (_currentPhase == EnrollmentPhase.Failed)
                    {
                        _logger.Warning("Enrollment failed - emitting failure event");
                        var failureEvent = new EnrollmentEvent
                        {
                            SessionId = _sessionId,
                            TenantId = _tenantId,
                            Timestamp = DateTime.UtcNow,
                            EventType = "enrollment_failed",
                            Severity = EventSeverity.Critical,
                            Source = "PhaseDetector",
                            Phase = EnrollmentPhase.Failed,
                            Message = "Autopilot enrollment failed"
                        };
                        _onEventCollected(failureEvent);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Error detecting phase", ex);
            }
        }

        private EnrollmentPhase DetectCurrentPhase()
        {
            // Try to detect phase from various indicators

            // 1. Check ESP completion status first (highest priority)
            try
            {
                using (var enrollmentsKey = Registry.LocalMachine.OpenSubKey(EspKeyPath, false))
                {
                    if (enrollmentsKey != null)
                    {
                        var subKeyNames = enrollmentsKey.GetSubKeyNames();
                        foreach (var subKeyName in subKeyNames)
                        {
                            using (var enrollmentKey = enrollmentsKey.OpenSubKey(subKeyName, false))
                            {
                                if (enrollmentKey != null)
                                {
                                    // Check for ESP tracking policies
                                    using (var statusKey = enrollmentKey.OpenSubKey("Status", false))
                                    {
                                        if (statusKey != null)
                                        {
                                            // Check for completion indicators
                                            var espComplete = statusKey.GetValue("EnrollmentStatusPageComplete");
                                            var deviceSetupStatus = statusKey.GetValue("DeviceSetupStatus");
                                            var accountSetupStatus = statusKey.GetValue("AccountSetupStatus");

                                            // Check for failure
                                            var hasError = statusKey.GetValue("HasError");
                                            if (hasError != null && Convert.ToInt32(hasError) > 0)
                                            {
                                                return EnrollmentPhase.Failed;
                                            }

                                            // Check for completion (ESP finished successfully)
                                            if (espComplete != null && Convert.ToInt32(espComplete) > 0)
                                            {
                                                return EnrollmentPhase.Complete;
                                            }

                                            // Check phase progression
                                            if (accountSetupStatus != null)
                                            {
                                                var accountStatus = Convert.ToInt32(accountSetupStatus);
                                                // 2 = Completed, check if this is final phase
                                                if (accountStatus == 2)
                                                {
                                                    return EnrollmentPhase.Complete;
                                                }
                                                return EnrollmentPhase.EspUserSetup;
                                            }

                                            if (deviceSetupStatus != null)
                                            {
                                                var deviceStatus = Convert.ToInt32(deviceSetupStatus);
                                                // Check for failure during device setup
                                                if (deviceStatus == 3) // 3 = Failed
                                                {
                                                    return EnrollmentPhase.Failed;
                                                }
                                                return EnrollmentPhase.EspDeviceSetup;
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

            // 2. Check Autopilot registry for deployment profile
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(AutopilotKeyPath, false))
                {
                    if (key != null)
                    {
                        var profileName = key.GetValue("DeploymentProfileName") as string;
                        if (!string.IsNullOrEmpty(profileName))
                        {
                            // Profile downloaded - at least in MDM Enrollment
                            return EnrollmentPhase.MdmEnrollment;
                        }
                    }
                }
            }
            catch { }

            // 3. If we have an Autopilot session running, we're at least in PreFlight
            return EnrollmentPhase.PreFlight;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
