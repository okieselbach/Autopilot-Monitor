#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Shared.Models;
using SharedConstants = AutopilotMonitor.Shared.Constants;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// Single-shot detector for the Hybrid User-Driven completion-gap (2026-05-01 trigger:
    /// session e58bcfdb-3e68-4f23-a3c2-437429ca9e78). After a Hybrid AAD-Join reboot the
    /// agent restarts and waits for the user to log in with their real AD/AAD account
    /// (which replaces the foouser/autopilot placeholder in JoinInfo). When that login is
    /// overdue the agent today goes silent until the backend 5-h watchdog fires; this
    /// detector emits a <c>hybrid_login_pending</c> warning event after a short single-shot
    /// timer so the operator sees an explicit "stuck waiting for AD login" signal in the
    /// timeline.
    /// <para>
    /// Conditions checked at <see cref="Arm"/>: composition root must have already
    /// confirmed a) post-reboot, b) <c>isHybridJoin == true</c>. The detector itself only
    /// owns the timer + <see cref="AadJoinWatcher.AadUserJoined"/> cancel-path. No polling.
    /// No periodicity. Fires at most once per agent process.
    /// </para>
    /// </summary>
    internal sealed class HybridLoginPendingDetector : IDisposable
    {
        internal const int DefaultDelayMinutes = 10;
        internal const string SourceLabel = "HybridLoginPendingDetector";

        private readonly AadJoinWatcher _watcher;
        private readonly InformationalEventPost _post;
        private readonly AgentLogger _logger;
        private readonly TimeSpan _delay;
        private readonly object _lock = new object();

        private Timer? _timer;
        private bool _armed;
        private bool _fired;
        private bool _cancelledByRealUser;
        private int _disposed;

        internal HybridLoginPendingDetector(
            AadJoinWatcher watcher,
            InformationalEventPost post,
            AgentLogger logger,
            TimeSpan? delay = null)
        {
            _watcher = watcher ?? throw new ArgumentNullException(nameof(watcher));
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _delay = delay ?? TimeSpan.FromMinutes(DefaultDelayMinutes);

            // Subscribe up-front so a real-user join that arrives BEFORE Arm() is still
            // recorded — _cancelledByRealUser short-circuits any later Arm() call.
            _watcher.AadUserJoined += OnAadUserJoined;
        }

        /// <summary>
        /// Starts the single-shot timer. Idempotent — repeated calls after the first arm
        /// are no-ops. Has no effect if the watcher already saw a real AAD user (the
        /// arming was racy and the cancel won), or if the detector already fired.
        /// </summary>
        public void Arm()
        {
            lock (_lock)
            {
                if (_disposed != 0) return;
                if (_armed || _fired) return;
                if (_cancelledByRealUser)
                {
                    _logger.Info("HybridLoginPendingDetector: arm requested but real AAD user already joined — skipped");
                    return;
                }

                _armed = true;
                _logger.Info(
                    $"HybridLoginPendingDetector: armed — will emit hybrid_login_pending in {_delay.TotalMinutes:F0} min if real AAD user has not joined by then");

                _timer = new Timer(OnTimer, null, _delay, Timeout.InfiniteTimeSpan);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { _watcher.AadUserJoined -= OnAadUserJoined; } catch { }
            lock (_lock)
            {
                _timer?.Dispose();
                _timer = null;
            }
        }

        // Test seam — bypasses the timer schedule. Same lock + state guards as the real path.
        internal void TriggerFromTest() => OnTimer(null);

        // Test seam — synthesises an AadUserJoined arrival without going through the
        // watcher's private event-raise path. The watcher's events can only be invoked from
        // inside the watcher class, so production unit tests have no way to observe the
        // detector's cancel-on-real-user behavior otherwise.
        internal void TriggerRealUserJoinedFromTest() =>
            OnAadUserJoined(this, new AadUserJoinedEventArgs("test@example.com", "test-thumbprint"));

        internal bool IsArmedForTest { get { lock (_lock) { return _armed; } } }
        internal bool IsCancelledByRealUserForTest { get { lock (_lock) { return _cancelledByRealUser; } } }
        internal bool HasFiredForTest { get { lock (_lock) { return _fired; } } }

        private void OnTimer(object? state) => EmitInternal(reason: "timer_fired");

        private void OnAadUserJoined(object sender, AadUserJoinedEventArgs e)
        {
            lock (_lock)
            {
                _cancelledByRealUser = true;
                if (!_armed || _fired) return;

                _timer?.Dispose();
                _timer = null;
                _logger.Info("HybridLoginPendingDetector: cancelled — real AAD user joined before timeout");
            }
        }

        private void EmitInternal(string reason)
        {
            lock (_lock)
            {
                // _armed guard (Codex review 2026-05-01): production is safe because the
                // timer is only created in Arm(), but explicit guard makes the contract
                // unambiguous — emission requires a deliberate Arm(), full stop.
                if (!_armed || _fired || _cancelledByRealUser || _disposed != 0) return;
                _fired = true;
                _timer?.Dispose();
                _timer = null;
            }

            var data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["delayMinutes"] = ((int)_delay.TotalMinutes).ToString(),
                ["reason"] = reason,
                ["isHybridJoin"] = "true",  // armed only when composition root verified this
            };

            _post.Emit(
                eventType: SharedConstants.EventTypes.HybridLoginPending,
                source: SourceLabel,
                message: $"Hybrid AAD Join: {(int)_delay.TotalMinutes} min after reboot still no real AD user — login overdue (placeholder still active in JoinInfo)",
                severity: EventSeverity.Warning,
                immediateUpload: true,
                data: data);

            _logger.Warning("HybridLoginPendingDetector: emitted hybrid_login_pending warning");
        }
    }
}
