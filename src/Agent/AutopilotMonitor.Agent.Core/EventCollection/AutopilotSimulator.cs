using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.Core.EventCollection
{
    /// <summary>
    /// Simulates a realistic Autopilot enrollment flow for testing and demo purposes
    /// </summary>
    public class AutopilotSimulator : IDisposable
    {
        private readonly AgentLogger _logger;
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly Action<EnrollmentEvent> _onEventCollected;
        private readonly bool _simulateFailure;
        private readonly Random _random = new Random();
        private CancellationTokenSource _cancellationTokenSource;
        private Task _simulationTask;
        private EnrollmentPhase _currentPhase = EnrollmentPhase.PreFlight;
        private bool _disposed = false;

        public AutopilotSimulator(
            string sessionId,
            string tenantId,
            Action<EnrollmentEvent> onEventCollected,
            AgentLogger logger,
            bool simulateFailure = false)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _onEventCollected = onEventCollected ?? throw new ArgumentNullException(nameof(onEventCollected));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _simulateFailure = simulateFailure;
        }

        /// <summary>
        /// Starts the Autopilot simulation
        /// </summary>
        public void Start()
        {
            _logger.Info("Starting Autopilot Simulator");
            _cancellationTokenSource = new CancellationTokenSource();
            _simulationTask = Task.Run(() => RunSimulation(_cancellationTokenSource.Token));
        }

        /// <summary>
        /// Stops the simulation
        /// </summary>
        public void Stop()
        {
            if (_cancellationTokenSource == null || _cancellationTokenSource.IsCancellationRequested)
                return;

            _logger.Info("Stopping Autopilot Simulator");

            try
            {
                _cancellationTokenSource?.Cancel();
                _simulationTask?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (ObjectDisposedException)
            {
                // Already disposed, ignore
            }
        }

        private async Task RunSimulation(CancellationToken cancellationToken)
        {
            try
            {
                _logger.Info("Starting simulated Autopilot enrollment flow");

                // Phase 1: Network
                await SimulatePhase(EnrollmentPhase.Network, cancellationToken);
                await EmitPhaseEvents(EnrollmentPhase.Network, new[]
                {
                    ("network_connected", "Network connection established", EventSeverity.Info),
                    ("wifi_connected", "Connected to WiFi network", EventSeverity.Info),
                    ("internet_check", "Internet connectivity verified", EventSeverity.Info)
                }, cancellationToken);

                // Phase 2: Identity
                await SimulatePhase(EnrollmentPhase.Identity, cancellationToken);
                await EmitPhaseEvents(EnrollmentPhase.Identity, new[]
                {
                    ("aad_join_start", "Starting Azure AD join", EventSeverity.Info),
                    ("device_registration", "Registering device with Azure AD", EventSeverity.Info),
                    ("certificate_request", "Requesting device certificate", EventSeverity.Info),
                    ("aad_join_complete", "Azure AD join completed successfully", EventSeverity.Info)
                }, cancellationToken);

                // Phase 3: MDM Enrollment
                await SimulatePhase(EnrollmentPhase.MdmEnrollment, cancellationToken);
                await EmitPhaseEvents(EnrollmentPhase.MdmEnrollment, new[]
                {
                    ("mdm_discovery", "Discovering MDM endpoints", EventSeverity.Info),
                    ("intune_enrollment_start", "Starting Intune enrollment", EventSeverity.Info),
                    ("policy_sync", "Syncing initial policies", EventSeverity.Info),
                    ("profile_download", "Downloaded Autopilot profile", EventSeverity.Info),
                    ("mdm_enrollment_complete", "MDM enrollment completed", EventSeverity.Info)
                }, cancellationToken);

                // Phase 4: ESP Device Setup
                await SimulatePhase(EnrollmentPhase.EspDeviceSetup, cancellationToken);

                if (_simulateFailure && _random.Next(0, 100) < 30)
                {
                    // Simulate a failure during device setup (30% chance if failure mode enabled)
                    await EmitPhaseEvents(EnrollmentPhase.EspDeviceSetup, new[]
                    {
                        ("esp_device_start", "Starting ESP Device Setup", EventSeverity.Info),
                        ("device_compliance_check", "Checking device compliance", EventSeverity.Info),
                        ("security_baseline_apply", "Applying security baseline", EventSeverity.Warning),
                        ("bitlocker_encryption", "BitLocker encryption failed", EventSeverity.Error),
                    }, cancellationToken);

                    await SimulatePhase(EnrollmentPhase.Failed, cancellationToken);
                    EmitEvent(new EnrollmentEvent
                    {
                        SessionId = _sessionId,
                        TenantId = _tenantId,
                        Timestamp = DateTime.UtcNow,
                        EventType = "enrollment_failed",
                        Severity = EventSeverity.Critical,
                        Source = "Simulator",
                        Phase = EnrollmentPhase.Failed,
                        Message = "Enrollment failed: BitLocker encryption error",
                        Data = new Dictionary<string, object>
                        {
                            { "errorCode", "0x80070002" },
                            { "failedPhase", "EspDeviceSetup" }
                        }
                    });

                    _logger.Warning("Simulated enrollment failure");
                    return;
                }

                await EmitPhaseEvents(EnrollmentPhase.EspDeviceSetup, new[]
                {
                    ("esp_device_start", "Starting ESP Device Setup", EventSeverity.Info),
                    ("device_compliance_check", "Checking device compliance", EventSeverity.Info),
                    ("security_baseline_apply", "Applying security baseline", EventSeverity.Info),
                    ("bitlocker_encryption", "Enabling BitLocker encryption", EventSeverity.Info),
                    ("device_config_apply", "Applying device configurations", EventSeverity.Info),
                    ("esp_device_complete", "ESP Device Setup completed", EventSeverity.Info)
                }, cancellationToken);

                // Phase 5: App Installation
                await SimulatePhase(EnrollmentPhase.AppInstallation, cancellationToken);
                await EmitPhaseEvents(EnrollmentPhase.AppInstallation, new[]
                {
                    ("app_install_start", "Starting application installation", EventSeverity.Info),
                    ("app_download_office", "Downloading Microsoft 365 Apps", EventSeverity.Info),
                    ("app_install_office", "Installing Microsoft 365 Apps", EventSeverity.Info),
                    ("app_download_teams", "Downloading Microsoft Teams", EventSeverity.Info),
                    ("app_install_teams", "Installing Microsoft Teams", EventSeverity.Info),
                    ("app_download_edge", "Downloading Microsoft Edge", EventSeverity.Info),
                    ("app_install_edge", "Installing Microsoft Edge", EventSeverity.Info),
                    ("app_install_complete", "All required apps installed", EventSeverity.Info)
                }, cancellationToken);

                // Phase 6: ESP User Setup
                await SimulatePhase(EnrollmentPhase.EspUserSetup, cancellationToken);
                await EmitPhaseEvents(EnrollmentPhase.EspUserSetup, new[]
                {
                    ("esp_user_start", "Starting ESP User Setup", EventSeverity.Info),
                    ("user_policy_sync", "Syncing user policies", EventSeverity.Info),
                    ("user_profile_setup", "Setting up user profile", EventSeverity.Info),
                    ("user_certs_install", "Installing user certificates", EventSeverity.Info),
                    ("esp_user_complete", "ESP User Setup completed", EventSeverity.Info)
                }, cancellationToken);

                // Phase 7: Complete
                await SimulatePhase(EnrollmentPhase.Complete, cancellationToken);
                EmitEvent(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId = _tenantId,
                    Timestamp = DateTime.UtcNow,
                    EventType = "enrollment_complete",
                    Severity = EventSeverity.Info,
                    Source = "Simulator",
                    Phase = EnrollmentPhase.Complete,
                    Message = "Autopilot enrollment completed successfully",
                    Data = new Dictionary<string, object>
                    {
                        { "totalDurationSeconds", 420 }, // ~7 minutes
                        { "appsInstalled", 3 },
                        { "policiesApplied", 15 }
                    }
                });

                _logger.Info("Simulated Autopilot enrollment completed successfully");
            }
            catch (OperationCanceledException)
            {
                _logger.Info("Autopilot simulation cancelled");
            }
            catch (Exception ex)
            {
                _logger.Error("Error in Autopilot simulation", ex);
            }
        }

        private async Task SimulatePhase(EnrollmentPhase phase, CancellationToken cancellationToken)
        {
            var previousPhase = _currentPhase;
            _currentPhase = phase;

            // Emit phase change event
            EmitEvent(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                Timestamp = DateTime.UtcNow,
                EventType = "phase_changed",
                Severity = EventSeverity.Info,
                Source = "Simulator",
                Phase = phase,
                Message = $"Enrollment phase changed: {previousPhase} -> {phase}",
                Data = new Dictionary<string, object>
                {
                    { "previousPhase", previousPhase.ToString() },
                    { "currentPhase", phase.ToString() },
                    { "phaseNumber", (int)phase }
                }
            });

            // Small delay between phases (2-5 seconds)
            await Task.Delay(TimeSpan.FromSeconds(_random.Next(2, 6)), cancellationToken);
        }

        private async Task EmitPhaseEvents(
            EnrollmentPhase phase,
            (string eventType, string message, EventSeverity severity)[] events,
            CancellationToken cancellationToken)
        {
            foreach (var (eventType, message, severity) in events)
            {
                EmitEvent(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId = _tenantId,
                    Timestamp = DateTime.UtcNow,
                    EventType = eventType,
                    Severity = severity,
                    Source = "Simulator",
                    Phase = phase,
                    Message = message,
                    Data = new Dictionary<string, object>
                    {
                        { "simulated", true },
                        { "phase", phase.ToString() }
                    }
                });

                // Random delay between events (1-3 seconds)
                await Task.Delay(TimeSpan.FromSeconds(_random.Next(1, 4)), cancellationToken);
            }
        }

        private void EmitEvent(EnrollmentEvent evt)
        {
            try
            {
                _onEventCollected(evt);
                _logger.Debug($"Simulator event: {evt.EventType} - {evt.Message}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Error emitting simulator event: {evt.EventType}", ex);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            Stop();
            _cancellationTokenSource?.Dispose();
            _disposed = true;
        }
    }
}
