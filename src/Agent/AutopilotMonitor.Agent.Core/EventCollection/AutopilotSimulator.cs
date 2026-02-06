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
        private CancellationTokenSource _perfCancellationTokenSource;
        private Task _simulationTask;
        private EnrollmentPhase _currentPhase = EnrollmentPhase.PreFlight;
        private bool _disposed = false;
        private double _simulatedDiskFreeGb;
        private readonly double _simulatedDiskTotalGb;

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
            _simulatedDiskTotalGb = 237.0 + _random.Next(0, 20); // ~237-256 GB typical SSD
            _simulatedDiskFreeGb = _simulatedDiskTotalGb - _random.Next(40, 60); // Start with 40-60 GB used
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

                // Start background performance snapshot emitter (with its own token so we can stop it on completion)
                _perfCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var perfTask = EmitPerformanceSnapshotsAsync(_perfCancellationTokenSource.Token);

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
                    StopPerformanceSnapshots();
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
                EmitEvent(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId = _tenantId,
                    Timestamp = DateTime.UtcNow,
                    EventType = "app_install_start",
                    Severity = EventSeverity.Info,
                    Source = "Simulator",
                    Phase = EnrollmentPhase.AppInstallation,
                    Message = "Starting application installation",
                    Data = new Dictionary<string, object> { { "simulated", true } }
                });
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);

                // Simulate realistic download progress for multiple apps
                await SimulateAppDownloads(cancellationToken);

                EmitEvent(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId = _tenantId,
                    Timestamp = DateTime.UtcNow,
                    EventType = "app_install_complete",
                    Severity = EventSeverity.Info,
                    Source = "Simulator",
                    Phase = EnrollmentPhase.AppInstallation,
                    Message = "All required apps installed",
                    Data = new Dictionary<string, object> { { "simulated", true }, { "appsInstalled", 5 } }
                });

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
                StopPerformanceSnapshots();
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

        /// <summary>
        /// Emits periodic performance_snapshot events throughout the simulation.
        /// Disk free GB decreases gradually as apps are installed.
        /// </summary>
        private async Task EmitPerformanceSnapshotsAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    // CPU: fluctuates between 5-95%, higher during app install phase
                    var baseCpu = _currentPhase == EnrollmentPhase.AppInstallation ? 40.0 : 15.0;
                    var cpuPercent = Math.Min(100, baseCpu + _random.Next(-10, 40));

                    // Memory: 8-16 GB total, usage increases over time
                    var memTotalMb = 16384.0; // 16 GB
                    var memUsedPercent = 35.0 + _random.Next(0, 25);
                    var memAvailMb = memTotalMb * (1.0 - memUsedPercent / 100.0);

                    // Disk queue: higher during installs
                    var diskQueue = _currentPhase == EnrollmentPhase.AppInstallation
                        ? 1.0 + _random.NextDouble() * 6.0
                        : _random.NextDouble() * 2.0;

                    // Disk free: smooth gradual decline throughout enrollment
                    // Small consistent decrements to produce a clean downward slope
                    if (_currentPhase == EnrollmentPhase.AppInstallation)
                    {
                        _simulatedDiskFreeGb -= (0.4 + _random.NextDouble() * 0.2); // 0.4-0.6 GB per snapshot (main install)
                    }
                    else if (_currentPhase == EnrollmentPhase.EspDeviceSetup)
                    {
                        _simulatedDiskFreeGb -= (0.15 + _random.NextDouble() * 0.1); // 0.15-0.25 GB (policies/configs)
                    }
                    else if (_currentPhase >= EnrollmentPhase.Network && _currentPhase <= EnrollmentPhase.MdmEnrollment)
                    {
                        _simulatedDiskFreeGb -= (0.05 + _random.NextDouble() * 0.05); // 0.05-0.1 GB (small overhead)
                    }

                    EmitEvent(new EnrollmentEvent
                    {
                        SessionId = _sessionId,
                        TenantId = _tenantId,
                        Timestamp = DateTime.UtcNow,
                        EventType = "performance_snapshot",
                        Severity = EventSeverity.Debug,
                        Source = "Simulator",
                        Phase = _currentPhase,
                        Message = $"CPU: {cpuPercent:F1}%, Memory: {memUsedPercent:F1}%, Disk Free: {_simulatedDiskFreeGb:F1} GB",
                        Data = new Dictionary<string, object>
                        {
                            { "cpu_percent", Math.Round(cpuPercent, 1) },
                            { "memory_available_mb", Math.Round(memAvailMb, 0) },
                            { "memory_total_mb", memTotalMb },
                            { "memory_used_percent", Math.Round(memUsedPercent, 1) },
                            { "disk_queue_length", Math.Round(diskQueue, 1) },
                            { "disk_free_gb", Math.Round(_simulatedDiskFreeGb, 1) },
                            { "disk_total_gb", Math.Round(_simulatedDiskTotalGb, 1) },
                            { "simulated", true }
                        }
                    });

                    await Task.Delay(TimeSpan.FromSeconds(_random.Next(8, 15)), cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when simulation ends
            }
        }

        /// <summary>
        /// Simulates realistic download progress for multiple apps with staggered starts,
        /// incremental progress updates, and proper completion events
        /// </summary>
        private async Task SimulateAppDownloads(CancellationToken cancellationToken)
        {
            var apps = new[]
            {
                new { Name = "Microsoft 365 Apps for Enterprise", SizeBytes = (long)(2.1 * 1024 * 1024 * 1024) },
                new { Name = "Microsoft Teams", SizeBytes = (long)(180 * 1024 * 1024) },
                new { Name = "Microsoft Edge", SizeBytes = (long)(160 * 1024 * 1024) },
                new { Name = "Company Portal", SizeBytes = (long)(45 * 1024 * 1024) },
                new { Name = "Global Protect VPN", SizeBytes = (long)(95 * 1024 * 1024) },
            };

            // Track progress per app
            var progress = new long[apps.Length];
            var completed = new bool[apps.Length];
            var started = new bool[apps.Length];
            // Stagger start: app 0 starts immediately, others start after delays
            var startAtTick = new int[apps.Length];
            startAtTick[0] = 0;
            for (int i = 1; i < apps.Length; i++)
            {
                startAtTick[i] = _random.Next(2, 5) + startAtTick[i - 1];
            }

            int tick = 0;
            while (!AllCompleted(completed))
            {
                cancellationToken.ThrowIfCancellationRequested();

                for (int i = 0; i < apps.Length; i++)
                {
                    if (completed[i]) continue;
                    if (tick < startAtTick[i]) continue;

                    if (!started[i])
                    {
                        started[i] = true;
                        // Emit start event
                        EmitDownloadProgressEvent(apps[i].Name, 0, apps[i].SizeBytes, 0, "downloading");
                        continue;
                    }

                    // Simulate download chunk: variable speed between 15-80 MB/s
                    var speedMbps = _random.Next(15, 80);
                    var chunkBytes = (long)(speedMbps * 1024 * 1024 * (2.0 + _random.NextDouble())); // ~2-3 seconds worth
                    progress[i] = Math.Min(progress[i] + chunkBytes, apps[i].SizeBytes);
                    var rateBps = speedMbps * 1024.0 * 1024.0;

                    if (progress[i] >= apps[i].SizeBytes)
                    {
                        // Completed
                        completed[i] = true;
                        EmitDownloadProgressEvent(apps[i].Name, apps[i].SizeBytes, apps[i].SizeBytes, 0, "completed");

                        // Also emit an install event
                        EmitEvent(new EnrollmentEvent
                        {
                            SessionId = _sessionId,
                            TenantId = _tenantId,
                            Timestamp = DateTime.UtcNow,
                            EventType = $"app_install_{apps[i].Name.ToLower().Replace(" ", "_")}",
                            Severity = EventSeverity.Info,
                            Source = "Simulator",
                            Phase = EnrollmentPhase.AppInstallation,
                            Message = $"Installed {apps[i].Name}",
                            Data = new Dictionary<string, object>
                            {
                                { "appName", apps[i].Name },
                                { "simulated", true }
                            }
                        });
                    }
                    else
                    {
                        EmitDownloadProgressEvent(apps[i].Name, progress[i], apps[i].SizeBytes, rateBps, "downloading");
                    }
                }

                tick++;
                await Task.Delay(TimeSpan.FromSeconds(_random.Next(2, 4)), cancellationToken);
            }
        }

        private static bool AllCompleted(bool[] completed)
        {
            for (int i = 0; i < completed.Length; i++)
                if (!completed[i]) return false;
            return true;
        }

        private void EmitDownloadProgressEvent(string appName, long bytesDownloaded, long bytesTotal, double rateBps, string status)
        {
            EmitEvent(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                Timestamp = DateTime.UtcNow,
                EventType = "download_progress",
                Severity = EventSeverity.Debug,
                Source = "Simulator",
                Phase = EnrollmentPhase.AppInstallation,
                Message = status == "completed"
                    ? $"Download complete: {appName}"
                    : $"Downloading {appName}: {(bytesTotal > 0 ? (bytesDownloaded * 100.0 / bytesTotal).ToString("F0") : "?")}%",
                Data = new Dictionary<string, object>
                {
                    { "app_name", appName },
                    { "bytes_downloaded", bytesDownloaded },
                    { "bytes_total", bytesTotal },
                    { "download_rate_bps", Math.Round(rateBps, 0) },
                    { "status", status },
                    { "simulated", true }
                }
            });
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

        private void StopPerformanceSnapshots()
        {
            try
            {
                _perfCancellationTokenSource?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            Stop();
            _perfCancellationTokenSource?.Dispose();
            _cancellationTokenSource?.Dispose();
            _disposed = true;
        }
    }
}
