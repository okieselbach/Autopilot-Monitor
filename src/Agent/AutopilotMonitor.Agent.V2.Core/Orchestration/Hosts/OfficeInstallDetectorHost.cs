#nullable enable
using System;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Office;
using AutopilotMonitor.DecisionCore.Engine;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Lifecycle host for the event-driven <see cref="OfficeInstallDetector"/> (Rev 3). Owns the OS
    /// watchers and orchestrates the pure detector core — no idle polling. The anchor is no longer the
    /// late, transient <c>OfficeC2RClient.exe</c> worker: the Office-CDN Delivery-Optimization job
    /// (surfaced by the DeliveryOptimizationCollector) and the <c>Scenario\INSTALL</c> registry key are
    /// caught much earlier, so <c>office_install_started</c> pairs to the real download start.
    /// <list type="bullet">
    ///   <item><see cref="RegistryChangeWatcher"/> armed at <see cref="Start"/> (bootstrap fallback
    ///     handles the not-yet-created ClickToRun key) → <c>OnRegistryChanged</c> (starts on
    ///     <c>Scenario\INSTALL</c>; progress / error otherwise) AND raises <see cref="OfficeExpected"/>
    ///     so the DO collector wakes to look for the Office-CDN job.</item>
    ///   <item><see cref="OfficeProcessWatcher"/> Started → <c>OnWorkerStarted</c> (an idempotent start
    ///     trigger; cancels any pending close — the install is clearly still active). Stopped → arms the
    ///     close schedule.</item>
    ///   <item><see cref="SubmitDoSample"/> (from the DO collector) → <c>OnOfficeDoSample</c> — the first
    ///     sample with jobs starts the lifecycle; later samples fold a real download-% into progress.</item>
    ///   <item><see cref="NotifyOfficeDownloadEnded"/> (from the DO collector) → arms the close schedule.</item>
    /// </list>
    /// <para>
    /// <b>Close + completion probe</b>: once the download has ended AND no worker is running, a settle
    /// timer fires <c>TryFinalizeCompletion</c>. Completion is proven by the core Office binaries on disk
    /// (C2R lays them down in the integrate phase, which can lag the download end), so a
    /// <see cref="OfficeInstallDetector.CompletionOutcome.NotYet"/> result is retried on a bounded probe
    /// schedule before the lifecycle is abandoned silently (never a false completed/failed).
    /// </para>
    /// </summary>
    internal sealed class OfficeInstallDetectorHost : ICollectorHost
    {
        private const string ClickToRunSubKey = @"SOFTWARE\Microsoft\Office\ClickToRun";

        // Post-download completion probe schedule (the C2R integrate phase lays the binaries down after
        // the stream ends). settleSeconds debounces the first attempt; then up to MaxCompletionProbes
        // re-checks at CompletionProbeIntervalSeconds — generous enough for the lay-down, bounded so we
        // never probe forever.
        private const int CompletionProbeIntervalSeconds = 15;
        private const int MaxCompletionProbes = 8;

        public string Name => OfficeInstallDetector.SourceName;

        private readonly OfficeInstallDetector _detector;
        private readonly OfficeProcessWatcher _processWatcher;
        private readonly AgentLogger _logger;
        private readonly int _settleSeconds;
        private readonly object _lock = new object();

        private RegistryChangeWatcher? _registryWatcher;
        private Timer? _closeTimer;
        private bool _downloadEnded;
        private bool _workerActive;
        private int _probeAttempts;
        private bool _lifecycleEnded;
        private int _disposed;

        /// <summary>
        /// Raised when a ClickToRun registry change suggests an Office install is imminent/active. The
        /// DeliveryOptimizationHost subscribes and wakes its collector to probe for the Office-CDN job.
        /// </summary>
        public event EventHandler? OfficeExpected;

        public OfficeInstallDetectorHost(
            string sessionId,
            string tenantId,
            ISignalIngressSink ingress,
            IClock clock,
            AgentLogger logger,
            int settleSeconds)
        {
            if (ingress == null) throw new ArgumentNullException(nameof(ingress));
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settleSeconds = settleSeconds;

            var post = new InformationalEventPost(ingress, clock);
            _detector = new OfficeInstallDetector(sessionId, tenantId, post, logger, clock);
            _processWatcher = new OfficeProcessWatcher(logger, settleSeconds);
            _processWatcher.Started += OnWorkerStarted;
            _processWatcher.Stopped += OnWorkerStopped;
        }

        /// <summary>The shared Office worker start/stop signal — the DeliveryOptimizationHost subscribes.</summary>
        public OfficeProcessWatcher ProcessWatcher => _processWatcher;

        /// <summary>Fold aggregated Office DO stats (from the DO collector) into the office_install_* events
        /// — the first sample with jobs also starts the lifecycle.</summary>
        public void SubmitDoSample(OfficeDoSample sample) => _detector.OnOfficeDoSample(sample);

        /// <summary>The Office-CDN download has ended (jobs gone / 100%) — arm the close schedule.</summary>
        public void NotifyOfficeDownloadEnded()
        {
            lock (_lock) { _downloadEnded = true; MaybeArmClose(); }
        }

        public void Start()
        {
            // Arm the registry watcher up front (not at worker-start) so the early Scenario\INSTALL key
            // is caught before the (late) worker process. The watcher's bootstrap fallback handles the
            // case where the ClickToRun key does not exist yet on a clean first install.
            lock (_lock)
            {
                _registryWatcher = new RegistryChangeWatcher(ClickToRunSubKey, _logger);
                _registryWatcher.Changed += OnRegistryChanged;
                _registryWatcher.Start();
            }
            _processWatcher.Start();
        }

        private void OnWorkerStarted(object? sender, EventArgs e)
        {
            lock (_lock)
            {
                _workerActive = true;
                // A worker is running → the install is clearly still active; cancel any pending close.
                CancelCloseTimer();
            }
            _detector.OnWorkerStarted();
        }

        private void OnWorkerStopped(object? sender, EventArgs e)
        {
            lock (_lock)
            {
                _workerActive = false;
                MaybeArmClose();
            }
        }

        private void OnRegistryChanged(object? sender, EventArgs e)
        {
            _detector.OnRegistryChanged();
            // Wake the DO collector to look for the Office-CDN job (until the lifecycle has ended).
            bool raise;
            lock (_lock) { raise = !_lifecycleEnded; }
            if (raise)
            {
                try { OfficeExpected?.Invoke(this, EventArgs.Empty); }
                catch (Exception ex) { _logger.Warning($"[OfficeInstallDetectorHost] OfficeExpected handler threw: {ex.Message}"); }
            }
        }

        // -----------------------------------------------------------------------
        // Close + bounded completion probe. All state mutated under _lock.
        // -----------------------------------------------------------------------

        private void MaybeArmClose()
        {
            if (_lifecycleEnded || _closeTimer != null) return;
            if (!_downloadEnded || _workerActive) return; // close only once download ended AND worker gone
            _probeAttempts = 0;
            _logger.Debug($"[OfficeInstallDetectorHost] download ended + worker gone — settling {_settleSeconds}s before completion check");
            _closeTimer = new Timer(OnCloseTimer, null, TimeSpan.FromSeconds(Math.Max(0, _settleSeconds)), Timeout.InfiniteTimeSpan);
        }

        private void CancelCloseTimer()
        {
            _closeTimer?.Dispose();
            _closeTimer = null;
        }

        private void OnCloseTimer(object? state)
        {
            lock (_lock)
            {
                _closeTimer?.Dispose();
                _closeTimer = null;
                if (_lifecycleEnded || _disposed != 0) return;
                // A worker reappeared during settle → the install is still going; stand down.
                if (_workerActive) return;
            }

            var outcome = _detector.TryFinalizeCompletion();
            lock (_lock)
            {
                if (_lifecycleEnded || _disposed != 0) return;
                if (outcome == OfficeInstallDetector.CompletionOutcome.NotYet)
                {
                    if (++_probeAttempts <= MaxCompletionProbes)
                    {
                        _logger.Debug($"[OfficeInstallDetectorHost] no on-disk completion proof yet — probe {_probeAttempts}/{MaxCompletionProbes} in {CompletionProbeIntervalSeconds}s");
                        _closeTimer = new Timer(OnCloseTimer, null, TimeSpan.FromSeconds(CompletionProbeIntervalSeconds), Timeout.InfiniteTimeSpan);
                        return;
                    }
                    // Exhausted — stop without emitting a terminal (conservative, no false completed/failed).
                    _detector.AbandonSilently();
                }
                _lifecycleEnded = true;
            }
            DisposeRegistryWatcher();
        }

        public void Stop() => Dispose();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { _processWatcher.Started -= OnWorkerStarted; } catch { }
            try { _processWatcher.Stopped -= OnWorkerStopped; } catch { }
            try { _processWatcher.Dispose(); } catch { }
            lock (_lock)
            {
                CancelCloseTimer();
            }
            DisposeRegistryWatcher();
        }

        private void DisposeRegistryWatcher()
        {
            lock (_lock)
            {
                if (_registryWatcher != null)
                {
                    try { _registryWatcher.Changed -= OnRegistryChanged; } catch { }
                    try { _registryWatcher.Dispose(); } catch { }
                    _registryWatcher = null;
                }
            }
        }
    }
}
