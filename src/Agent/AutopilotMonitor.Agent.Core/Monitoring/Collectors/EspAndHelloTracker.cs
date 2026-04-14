using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Threading;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Agent.Core.Monitoring.Core;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.Core.Monitoring.Collectors
{
    /// <summary>
    /// Coordinates ESP (Enrollment Status Page) exit detection and Windows Hello for Business (WHfB)
    /// provisioning tracking during Autopilot enrollment, including WhiteGlove (Pre-Provisioning) detection.
    ///
    /// Composed of three focused sub-trackers:
    ///   - <see cref="HelloTracker"/> — WHfB policy detection, UDR 300/301/358/360/362/376, HelloForBusiness 3024/6045, Hello timers
    ///   - <see cref="ModernDeploymentTracker"/> — ModernDeployment-Diagnostics-Provider live capture + WhiteGlove Event 509
    ///   - Shell-Core + Provisioning (remaining partials) — ESP exit / failure / provisioning category monitoring
    ///
    /// Key Event IDs (Microsoft-Windows-Shell-Core/Operational):
    ///   62404 - CloudExperienceHost Web App Activity Started (CXID: 'AADHello' or 'NGC' - Hello wizard started)
    ///   62407 - CloudExperienceHost Web App Event 2:
    ///           CommercialOOBE_ESPProgress_Page_Exiting      — normal ESP exit
    ///           CommercialOOBE_ESPProgress_WhiteGlove_Success — WhiteGlove (Pre-Provisioning) complete
    /// </summary>
    public partial class EspAndHelloTracker : IDisposable
    {
        private readonly AgentLogger _logger;
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly Action<EnrollmentEvent> _onEventCollected;
        private EventLogWatcher _shellCoreWatcher;
        private RegistryWatcher _provisioningWatcher;
        private System.Threading.Timer _provisioningWatcherRetryTimer;
        private System.Threading.Timer _provisioningDebounceTimer;
        private ModernDeploymentTracker _modernDeploymentTracker;
        private HelloTracker _helloTracker;

        private bool _espExitDetected = false;
        private bool _whiteGloveDetected = false;
        private string _detectedEspFailureType; // set during Shell-Core event processing, consumed after event emission
        private readonly object _stateLock = new object();

        private readonly int _helloWaitTimeoutSeconds;
        private readonly bool _modernDeploymentWatcherEnabled;
        private readonly int _modernDeploymentLogLevelMax;
        private readonly bool _modernDeploymentBackfillEnabled;
        private readonly int _modernDeploymentBackfillLookbackMinutes;
        private readonly string _stateDirectory;

        /// <summary>
        /// Callback invoked when Hello provisioning completes (successfully or failed)
        /// Based on events 300/301 only, NOT on event 360 (which is just a snapshot)
        /// </summary>
        public event EventHandler HelloCompleted;

        /// <summary>
        /// Callback invoked when ESP exit or Hello wizard start is detected
        /// Triggers transition to FinalizingSetup phase
        /// </summary>
        public event EventHandler<string> FinalizingSetupPhaseTriggered;

        /// <summary>
        /// Fired when WhiteGlove (Pre-Provisioning) completes successfully.
        /// The device will shut down; the agent should terminate gracefully.
        /// </summary>
        public event EventHandler WhiteGloveCompleted;

        /// <summary>
        /// Fired when an ESP failure is detected (ESPProgress_Failure, _Timeout, _Abort, WhiteGlove_Failed, etc.).
        /// The string argument is the structured failure type (e.g. "ESPProgress_Failure", "ESPProgress_Timeout").
        /// </summary>
        public event EventHandler<string> EspFailureDetected;

        /// <summary>
        /// Fired when DeviceSetup provisioning status shows categorySucceeded=true.
        /// Used as a completion signal for Self-Deploying mode where Shell-Core ESP exit
        /// and desktop arrival signals may never arrive.
        /// </summary>
        public event EventHandler DeviceSetupProvisioningComplete;

        /// <summary>
        /// Outcome of Hello provisioning. Set when Hello resolves (via events, timeout, or not configured).
        /// Values: "completed", "skipped", "timeout", "not_configured", "wizard_not_started", null (not yet resolved).
        /// </summary>
        public string HelloOutcome => _helloTracker?.HelloOutcome;

        private const string ShellCoreEventLogChannel = "Microsoft-Windows-Shell-Core/Operational";

        // Tracked event IDs (Shell-Core/Operational)
        private const int EventId_ShellCore_WebAppStarted = 62404;
        private const int EventId_ShellCore_WebAppEvent = 62407;

        private static readonly HashSet<int> TrackedShellCoreEventIds = new HashSet<int>
        {
            EventId_ShellCore_WebAppStarted,
            EventId_ShellCore_WebAppEvent
        };

        public EspAndHelloTracker(
            string sessionId,
            string tenantId,
            Action<EnrollmentEvent> onEventCollected,
            AgentLogger logger,
            int helloWaitTimeoutSeconds = 30,
            bool modernDeploymentWatcherEnabled = true,
            int modernDeploymentLogLevelMax = 3,
            bool modernDeploymentBackfillEnabled = true,
            int modernDeploymentBackfillLookbackMinutes = 30,
            string stateDirectory = null)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _onEventCollected = onEventCollected ?? throw new ArgumentNullException(nameof(onEventCollected));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _helloWaitTimeoutSeconds = helloWaitTimeoutSeconds;
            _modernDeploymentWatcherEnabled = modernDeploymentWatcherEnabled;
            _modernDeploymentLogLevelMax = modernDeploymentLogLevelMax;
            _modernDeploymentBackfillEnabled = modernDeploymentBackfillEnabled;
            _modernDeploymentBackfillLookbackMinutes = modernDeploymentBackfillLookbackMinutes;
            _stateDirectory = stateDirectory != null ? Environment.ExpandEnvironmentVariables(stateDirectory) : null;
        }

        /// <summary>
        /// Gets whether Windows Hello for Business policy is configured (enabled or disabled)
        /// </summary>
        public bool IsPolicyConfigured => _helloTracker?.IsPolicyConfigured ?? false;

        /// <summary>
        /// Gets whether Windows Hello provisioning has completed (successfully, failed, or skipped)
        /// </summary>
        public bool IsHelloCompleted => _helloTracker?.IsHelloCompleted ?? false;

        /// <summary>
        /// Force-marks Hello as completed from an external caller (e.g. safety timeout in EnrollmentTracker).
        /// Does NOT invoke the HelloCompleted event — the caller handles completion logic directly.
        /// </summary>
        public void ForceMarkHelloCompleted(string reason)
        {
            _helloTracker?.ForceMarkHelloCompleted(reason);
        }

        /// <summary>
        /// Starts the Hello wait timer. Should be called by EnrollmentTracker when AccountSetup phase exits.
        /// </summary>
        public void StartHelloWaitTimer()
        {
            _helloTracker?.StartHelloWaitTimer();
        }

        /// <summary>
        /// Resets Hello tracking state when ESP resumes after a mid-enrollment reboot (hybrid join).
        /// Also clears the coordinator's ESP-exit flag so ESP exit detection fires again.
        /// </summary>
        public void ResetForEspResumption()
        {
            _helloTracker?.ResetForEspResumption();
            lock (_stateLock)
            {
                _espExitDetected = false;
            }
        }

        public void Start()
        {
            _logger.Info("Starting ESP and Hello tracker");

            // Compose HelloTracker (UDR watcher + HelloForBusiness watcher + policy check + Hello timers)
            _helloTracker = new HelloTracker(
                _sessionId,
                _tenantId,
                _onEventCollected,
                _logger,
                _helloWaitTimeoutSeconds);
            _helloTracker.HelloCompleted += OnHelloTrackerHelloCompleted;
            _helloTracker.Start();

            // Subscribe to Shell-Core/Operational event log for ESP exit and Hello wizard detection
            StartShellCoreEventLogWatcher();

            // Subscribe to ModernDeployment-Diagnostics-Provider channels (Autopilot + ManagementService)
            // for live capture of ESP/Autopilot events that Shell-Core/Registry watchers may miss.
            if (_modernDeploymentWatcherEnabled)
            {
                _modernDeploymentTracker = new ModernDeploymentTracker(
                    _sessionId,
                    _tenantId,
                    _onEventCollected,
                    _logger,
                    _modernDeploymentLogLevelMax,
                    _modernDeploymentBackfillEnabled,
                    _modernDeploymentBackfillLookbackMinutes,
                    _stateDirectory);
                _modernDeploymentTracker.Start();
            }

            // Watch ESP provisioning category status from registry via RegNotifyChangeKeyValue
            // (catches failures that Shell-Core event 62407 patterns miss, e.g. Certificate failures)
            StartProvisioningStatusWatcher();
        }

        public void Stop()
        {
            _logger.Info("Stopping ESP and Hello tracker");

            if (_helloTracker != null)
            {
                try
                {
                    _helloTracker.HelloCompleted -= OnHelloTrackerHelloCompleted;
                    _helloTracker.Stop();
                    _helloTracker = null;
                }
                catch (Exception ex)
                {
                    _logger.Error("Error stopping Hello tracker", ex);
                }
            }

            // Stop provisioning status registry watcher
            StopProvisioningStatusWatcher("tracker_stopped");

            // Stop Shell-Core/Operational watcher
            if (_shellCoreWatcher != null)
            {
                try
                {
                    _shellCoreWatcher.Enabled = false;
                    _shellCoreWatcher.EventRecordWritten -= OnShellCoreEventRecordWritten;
                    _shellCoreWatcher.Dispose();
                    _shellCoreWatcher = null;
                }
                catch (Exception ex)
                {
                    _logger.Error("Error stopping Shell-Core event watcher", ex);
                }
            }

            // Stop ModernDeployment-Diagnostics-Provider tracker (composed)
            if (_modernDeploymentTracker != null)
            {
                try
                {
                    _modernDeploymentTracker.Stop();
                    _modernDeploymentTracker = null;
                }
                catch (Exception ex)
                {
                    _logger.Error("Error stopping ModernDeployment tracker", ex);
                }
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private void OnHelloTrackerHelloCompleted(object sender, EventArgs e)
        {
            try { HelloCompleted?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { _logger.Error("Error forwarding HelloCompleted event", ex); }
        }
    }
}
