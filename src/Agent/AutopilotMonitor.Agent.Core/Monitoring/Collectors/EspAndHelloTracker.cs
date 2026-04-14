using System;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.Core.Monitoring.Collectors
{
    /// <summary>
    /// Thin coordinator that composes four focused sub-trackers to deliver the ESP + Hello
    /// monitoring surface expected by <c>CollectorCoordinator</c> and <c>EnrollmentTracker</c>:
    ///
    ///   - <see cref="HelloTracker"/>              — WHfB policy, UDR 300/301/358/360/362/376,
    ///                                                HelloForBusiness 3024/6045, Hello timers
    ///   - <see cref="ShellCoreTracker"/>          — Shell-Core 62404/62407 (ESP exit / failure,
    ///                                                WhiteGlove success, Hello wizard start)
    ///   - <see cref="ProvisioningStatusTracker"/> — Registry-driven ESP provisioning category
    ///                                                tracking + DeviceSetup fallback
    ///   - <see cref="ModernDeploymentTracker"/>   — ModernDeployment-Diagnostics-Provider live
    ///                                                capture + WhiteGlove Event 509 backfill
    ///
    /// Public API (events, properties, methods) is preserved byte-for-byte from the pre-split
    /// monolith so callers don't change.
    /// </summary>
    public sealed class EspAndHelloTracker : IDisposable
    {
        private readonly AgentLogger _logger;
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly Action<EnrollmentEvent> _onEventCollected;

        private readonly int _helloWaitTimeoutSeconds;
        private readonly bool _modernDeploymentWatcherEnabled;
        private readonly int _modernDeploymentLogLevelMax;
        private readonly bool _modernDeploymentBackfillEnabled;
        private readonly int _modernDeploymentBackfillLookbackMinutes;
        private readonly string _stateDirectory;
        private readonly int[] _modernDeploymentHarmlessEventIds;

        private HelloTracker _helloTracker;
        private ShellCoreTracker _shellCoreTracker;
        private ProvisioningStatusTracker _provisioningTracker;
        private ModernDeploymentTracker _modernDeploymentTracker;

        /// <summary>
        /// Callback invoked when Hello provisioning completes (successfully, failed, skipped, or timeout).
        /// Based on events 300/301/362 only — NOT on event 360 (which is just a snapshot).
        /// </summary>
        public event EventHandler HelloCompleted;

        /// <summary>
        /// Callback invoked when ESP exit or Hello wizard start is detected.
        /// Triggers transition to FinalizingSetup phase in <c>EnrollmentTracker</c>.
        /// </summary>
        public event EventHandler<string> FinalizingSetupPhaseTriggered;

        /// <summary>
        /// Fired when WhiteGlove (Pre-Provisioning) completes successfully.
        /// The device will shut down; the agent should terminate gracefully.
        /// </summary>
        public event EventHandler WhiteGloveCompleted;

        /// <summary>
        /// Fired when an ESP failure is detected (ESPProgress_Failure, _Timeout, _Abort,
        /// WhiteGlove_Failed, Provisioning_*_Failed, etc.). The string is the structured failure type.
        /// </summary>
        public event EventHandler<string> EspFailureDetected;

        /// <summary>
        /// Fired when DeviceSetup provisioning status shows categorySucceeded=true (or fallback confirmed).
        /// Used as a completion signal for Self-Deploying mode where Shell-Core ESP exit and
        /// desktop arrival signals may never arrive.
        /// </summary>
        public event EventHandler DeviceSetupProvisioningComplete;

        /// <summary>
        /// Outcome of Hello provisioning. Set when Hello resolves (via events, timeout, or not configured).
        /// Values: "completed", "skipped", "timeout", "not_configured", "wizard_not_started", null (not yet resolved).
        /// </summary>
        public string HelloOutcome => _helloTracker?.HelloOutcome;

        /// <summary>
        /// True when Windows Hello for Business policy is configured (enabled or disabled).
        /// </summary>
        public bool IsPolicyConfigured => _helloTracker?.IsPolicyConfigured ?? false;

        /// <summary>
        /// True when Windows Hello provisioning has completed (successfully, failed, or skipped).
        /// </summary>
        public bool IsHelloCompleted => _helloTracker?.IsHelloCompleted ?? false;

        /// <summary>
        /// True when DeviceSetupCategory.Status has resolved categorySucceeded=true OR the fallback confirmed.
        /// </summary>
        public bool DeviceSetupCategorySucceeded => _provisioningTracker?.DeviceSetupCategorySucceeded ?? false;

        /// <summary>
        /// True when any AccountSetup subcategory has been tracked (resolved or in progress).
        /// </summary>
        public bool HasAccountSetupActivity => _provisioningTracker?.HasAccountSetupActivity ?? false;

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
            string stateDirectory = null,
            int[] modernDeploymentHarmlessEventIds = null)
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
            _modernDeploymentHarmlessEventIds = modernDeploymentHarmlessEventIds;
        }

        // =====================================================================
        // Forwarded state/snapshot methods
        // =====================================================================

        public System.Collections.Generic.Dictionary<string, bool?> GetProvisioningCategorySnapshot()
            => _provisioningTracker?.GetProvisioningCategorySnapshot()
               ?? new System.Collections.Generic.Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);

        public EspProvisioningSnapshot GetProvisioningSnapshot() => _provisioningTracker?.GetProvisioningSnapshot();

        // =====================================================================
        // Forwarded Hello coordination API
        // =====================================================================

        /// <summary>
        /// Force-marks Hello as completed from an external caller (e.g. safety timeout in EnrollmentTracker).
        /// Does NOT invoke the HelloCompleted event — the caller handles completion logic directly.
        /// </summary>
        public void ForceMarkHelloCompleted(string reason) => _helloTracker?.ForceMarkHelloCompleted(reason);

        /// <summary>
        /// Starts the Hello wait timer. Called by EnrollmentTracker when AccountSetup phase exits.
        /// </summary>
        public void StartHelloWaitTimer() => _helloTracker?.StartHelloWaitTimer();

        /// <summary>
        /// Resets Hello tracking state when ESP resumes after a mid-enrollment reboot (hybrid join).
        /// </summary>
        public void ResetForEspResumption() => _helloTracker?.ResetForEspResumption();

        /// <summary>
        /// Backfills recent ESP exit and failure events from Shell-Core log on startup.
        /// Secondary recovery mechanism when state persistence is unavailable.
        /// </summary>
        public void BackfillRecentEspExitEvents() => _shellCoreTracker?.BackfillRecentEspExitEvents();

        // =====================================================================
        // Lifecycle
        // =====================================================================

        public void Start()
        {
            _logger.Info("Starting ESP and Hello tracker");

            _helloTracker = new HelloTracker(
                _sessionId,
                _tenantId,
                _onEventCollected,
                _logger,
                _helloWaitTimeoutSeconds);
            _helloTracker.HelloCompleted += OnHelloCompleted;
            _helloTracker.Start();

            _shellCoreTracker = new ShellCoreTracker(
                _sessionId,
                _tenantId,
                _onEventCollected,
                _logger,
                _helloTracker);
            _shellCoreTracker.FinalizingSetupPhaseTriggered += OnFinalizingSetupPhaseTriggered;
            _shellCoreTracker.WhiteGloveCompleted += OnWhiteGloveCompleted;
            _shellCoreTracker.EspFailureDetected += OnEspFailureDetected;
            _shellCoreTracker.Start();

            _provisioningTracker = new ProvisioningStatusTracker(
                _sessionId,
                _tenantId,
                _onEventCollected,
                _logger);
            _provisioningTracker.EspFailureDetected += OnEspFailureDetected;
            _provisioningTracker.DeviceSetupProvisioningComplete += OnDeviceSetupProvisioningComplete;
            _provisioningTracker.Start();

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
                    _stateDirectory,
                    _modernDeploymentHarmlessEventIds);
                _modernDeploymentTracker.Start();
            }
        }

        public void Stop()
        {
            _logger.Info("Stopping ESP and Hello tracker");

            DisposeTracker(ref _modernDeploymentTracker, "ModernDeployment", t => t.Stop());
            DisposeProvisioningTracker();
            DisposeShellCoreTracker();
            DisposeHelloTracker();
        }

        public void Dispose() => Stop();

        private void DisposeHelloTracker()
        {
            if (_helloTracker == null) return;
            try
            {
                _helloTracker.HelloCompleted -= OnHelloCompleted;
                _helloTracker.Stop();
            }
            catch (Exception ex) { _logger.Error("Error stopping Hello tracker", ex); }
            _helloTracker = null;
        }

        private void DisposeShellCoreTracker()
        {
            if (_shellCoreTracker == null) return;
            try
            {
                _shellCoreTracker.FinalizingSetupPhaseTriggered -= OnFinalizingSetupPhaseTriggered;
                _shellCoreTracker.WhiteGloveCompleted -= OnWhiteGloveCompleted;
                _shellCoreTracker.EspFailureDetected -= OnEspFailureDetected;
                _shellCoreTracker.Stop();
            }
            catch (Exception ex) { _logger.Error("Error stopping Shell-Core tracker", ex); }
            _shellCoreTracker = null;
        }

        private void DisposeProvisioningTracker()
        {
            if (_provisioningTracker == null) return;
            try
            {
                _provisioningTracker.EspFailureDetected -= OnEspFailureDetected;
                _provisioningTracker.DeviceSetupProvisioningComplete -= OnDeviceSetupProvisioningComplete;
                _provisioningTracker.Stop("tracker_stopped");
            }
            catch (Exception ex) { _logger.Error("Error stopping provisioning status tracker", ex); }
            _provisioningTracker = null;
        }

        private void DisposeTracker<T>(ref T tracker, string name, Action<T> stopper) where T : class
        {
            if (tracker == null) return;
            try { stopper(tracker); }
            catch (Exception ex) { _logger.Error($"Error stopping {name} tracker", ex); }
            tracker = null;
        }

        // =====================================================================
        // Event forwarders
        // =====================================================================

        private void OnHelloCompleted(object sender, EventArgs e)
        {
            try { HelloCompleted?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { _logger.Error("Error forwarding HelloCompleted", ex); }
        }

        private void OnFinalizingSetupPhaseTriggered(object sender, string reason)
        {
            try { FinalizingSetupPhaseTriggered?.Invoke(this, reason); }
            catch (Exception ex) { _logger.Error("Error forwarding FinalizingSetupPhaseTriggered", ex); }
        }

        private void OnWhiteGloveCompleted(object sender, EventArgs e)
        {
            try { WhiteGloveCompleted?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { _logger.Error("Error forwarding WhiteGloveCompleted", ex); }
        }

        private void OnEspFailureDetected(object sender, string failureType)
        {
            try { EspFailureDetected?.Invoke(this, failureType); }
            catch (Exception ex) { _logger.Error($"Error forwarding EspFailureDetected for '{failureType}'", ex); }
        }

        private void OnDeviceSetupProvisioningComplete(object sender, EventArgs e)
        {
            try { DeviceSetupProvisioningComplete?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { _logger.Error("Error forwarding DeviceSetupProvisioningComplete", ex); }
        }
    }
}
