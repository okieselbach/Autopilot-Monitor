using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Threading;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Agent.Core.Monitoring.Core;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.Core.Monitoring.Collectors
{
    /// <summary>
    /// Tracks ESP (Enrollment Status Page) exit events and Windows Hello for Business (WHfB)
    /// provisioning during Autopilot enrollment, including WhiteGlove (Pre-Provisioning) detection.
    ///
    /// Key Event IDs (User Device Registration/Admin):
    ///   358 - Prerequisites passed, provisioning will be launched
    ///   360 - Prerequisites failed, provisioning will NOT be launched (SNAPSHOT ONLY - not terminal)
    ///   362 - Provisioning blocked
    ///   300 - NGC key registered successfully (Hello provisioned)
    ///   301 - NGC key registration failed
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
        private System.Diagnostics.Eventing.Reader.EventLogWatcher _watcher;
        private System.Diagnostics.Eventing.Reader.EventLogWatcher _shellCoreWatcher;
        private System.Threading.Timer _policyCheckTimer;
        private System.Threading.Timer _helloWaitTimer;
        private System.Threading.Timer _helloCompletionTimer;
        private RegistryWatcher _provisioningWatcher;
        private System.Threading.Timer _provisioningWatcherRetryTimer;

        private bool _isPolicyConfigured = false;
        private bool _isHelloPolicyEnabled = false; // true when Hello policy is explicitly detected as enabled
        private bool _isHelloCompleted = false;
        private bool _espExitDetected = false;
        private bool _helloWizardStarted = false;
        private bool _whiteGloveDetected = false;
        private string _detectedEspFailureType; // set during Shell-Core event processing, consumed after event emission
        private readonly object _stateLock = new object();

        private readonly int _helloWaitTimeoutSeconds;
        private const int HelloCompletionTimeoutSeconds = 300;
        private const int BackfillLookbackMinutes = 5;

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
        /// Values: "completed", "timeout", "not_configured", "wizard_not_started", null (not yet resolved).
        /// </summary>
        public string HelloOutcome { get; private set; }

        private const string EventLogChannel = "Microsoft-Windows-User Device Registration/Admin";
        private const string ShellCoreEventLogChannel = "Microsoft-Windows-Shell-Core/Operational";

        // WHfB policy registry paths
        private const string CspPolicyBasePath = @"SOFTWARE\Microsoft\Policies\PassportForWork";
        private const string GpoPolicyPath = @"SOFTWARE\Policies\Microsoft\PassportForWork";

        // Tracked event IDs (User Device Registration)
        private const int EventId_NgcKeyRegistered = 300;
        private const int EventId_NgcKeyRegistrationFailed = 301;
        private const int EventId_ProvisioningWillLaunch = 358;
        private const int EventId_ProvisioningWillNotLaunch = 360;
        private const int EventId_ProvisioningBlocked = 362;
        private const int EventId_PinStatus = 376;

        // Tracked event IDs (Shell-Core/Operational)
        private const int EventId_ShellCore_WebAppStarted = 62404;
        private const int EventId_ShellCore_WebAppEvent = 62407;

        private static readonly HashSet<int> TrackedEventIds = new HashSet<int>
        {
            EventId_NgcKeyRegistered,
            EventId_NgcKeyRegistrationFailed,
            EventId_ProvisioningWillLaunch,
            EventId_ProvisioningWillNotLaunch,
            EventId_ProvisioningBlocked,
            EventId_PinStatus
        };

        private static readonly HashSet<int> TrackedShellCoreEventIds = new HashSet<int>
        {
            EventId_ShellCore_WebAppStarted,
            EventId_ShellCore_WebAppEvent
        };

        public EspAndHelloTracker(string sessionId, string tenantId, Action<EnrollmentEvent> onEventCollected, AgentLogger logger, int helloWaitTimeoutSeconds = 30)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _onEventCollected = onEventCollected ?? throw new ArgumentNullException(nameof(onEventCollected));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _helloWaitTimeoutSeconds = helloWaitTimeoutSeconds;
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

        /// <summary>
        /// Force-marks Hello as completed from an external caller (e.g. safety timeout in EnrollmentTracker).
        /// Does NOT invoke the HelloCompleted event — the caller handles completion logic directly.
        /// </summary>
        public void ForceMarkHelloCompleted(string reason)
        {
            lock (_stateLock)
            {
                if (_isHelloCompleted) return;
                _isHelloCompleted = true;
                HelloOutcome = reason;
                StopHelloCompletionTimerLocked();
                _logger.Warning($"Hello force-completed by external caller: {reason}");
            }
        }

        public void Start()
        {
            _logger.Info("Starting ESP and Hello tracker");

            // Check if WHfB policy is configured initially
            CheckHelloPolicy();

            // Start periodic policy check to detect policy arriving later via MDM
            _policyCheckTimer = new System.Threading.Timer(
                _ => CheckHelloPolicy(),
                null,
                TimeSpan.FromSeconds(10), // Initial delay before first check
                TimeSpan.FromSeconds(10)  // Subsequent checks every 10 seconds (fast detection, low cost)
            );

            // Subscribe to User Device Registration event log
            StartEventLogWatcher();

            // Subscribe to Shell-Core/Operational event log for ESP exit and Hello wizard detection
            StartShellCoreEventLogWatcher();

            // Safety net: backfill recent terminal events in case watcher started late or event delivery lagged.
            BackfillRecentTerminalHelloEvents();

            // Watch ESP provisioning category status from registry via RegNotifyChangeKeyValue
            // (catches failures that Shell-Core event 62407 patterns miss, e.g. Certificate failures)
            StartProvisioningStatusWatcher();
        }

        public void Stop()
        {
            _logger.Info("Stopping ESP and Hello tracker");

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
                    _logger.Error("Error stopping ESP and Hello tracker policy check timer", ex);
                }
            }

            // Stop Hello wait timer
            if (_helloWaitTimer != null)
            {
                try
                {
                    _helloWaitTimer.Dispose();
                    _helloWaitTimer = null;
                }
                catch (Exception ex)
                {
                    _logger.Error("Error stopping Hello wait timer", ex);
                }
            }

            // Stop Hello completion timer
            if (_helloCompletionTimer != null)
            {
                try
                {
                    _helloCompletionTimer.Dispose();
                    _helloCompletionTimer = null;
                }
                catch (Exception ex)
                {
                    _logger.Error("Error stopping Hello completion timer", ex);
                }
            }

            // Stop User Device Registration watcher
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
                    _logger.Error("Error stopping ESP and Hello tracker watcher", ex);
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
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
