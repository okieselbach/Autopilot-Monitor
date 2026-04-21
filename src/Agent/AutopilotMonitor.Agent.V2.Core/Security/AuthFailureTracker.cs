using System;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.DecisionCore.Engine;

namespace AutopilotMonitor.Agent.V2.Core.Security
{
    /// <summary>
    /// Tracks consecutive 401/403 responses from the backend. When either the consecutive-count
    /// ceiling (<c>MaxAuthFailures</c>) or the elapsed-time ceiling (<c>AuthFailureTimeoutMinutes</c>)
    /// is exceeded, <see cref="ThresholdExceeded"/> fires exactly once so <c>Program.RunAgent</c>
    /// can trigger a soft shutdown.
    /// <para>
    /// Without this tracker a device whose certificate is revoked (or whose tenant has been deleted)
    /// would retry the config fetch and every telemetry upload indefinitely, flooding the backend
    /// distress channel and the local log. The tracker is the missing enforcement for the
    /// <c>MaxAuthFailures</c> and <c>AuthFailureTimeoutMinutes</c> knobs on
    /// <c>AgentConfigResponse</c> (defaults: 5 consecutive, time window disabled).
    /// </para>
    /// <para>
    /// Thread-safety: consecutive-count uses <see cref="Interlocked"/>; the first-failure timestamp
    /// is protected by a single monitor lock. <see cref="ThresholdExceeded"/> is raised outside the
    /// lock to prevent handler re-entrancy deadlocks.
    /// </para>
    /// </summary>
    public sealed class AuthFailureTracker
    {
        private readonly IClock _clock;
        private readonly AgentLogger _logger;
        private readonly object _windowLock = new object();

        private int _maxFailures;              // 0 = disabled
        private TimeSpan? _timeoutWindow;      // null = disabled
        private int _consecutiveFailures;
        private DateTime? _firstFailureUtc;
        private int _thresholdFired;           // 0/1 — Interlocked exchange makes the event single-shot

        public AuthFailureTracker(
            int maxFailures,
            int timeoutMinutes,
            IClock clock,
            AgentLogger logger)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            UpdateLimits(maxFailures, timeoutMinutes);
        }

        /// <summary>Fires once when either threshold is crossed. Listener is responsible for shutting the agent down.</summary>
        public event EventHandler<AuthFailureThresholdEventArgs> ThresholdExceeded;

        /// <summary>Updates the limits from the merged remote config. Safe to call at any time; does not reset the counter.</summary>
        public void UpdateLimits(int maxFailures, int timeoutMinutes)
        {
            _maxFailures = maxFailures < 0 ? 0 : maxFailures;
            _timeoutWindow = timeoutMinutes > 0 ? (TimeSpan?)TimeSpan.FromMinutes(timeoutMinutes) : null;
        }

        /// <summary>Current consecutive-failure count. Exposed for observability and tests.</summary>
        public int ConsecutiveFailures => Volatile.Read(ref _consecutiveFailures);

        /// <summary>Reset counter + window anchor after a successful authenticated response.</summary>
        public void RecordSuccess()
        {
            if (Interlocked.Exchange(ref _consecutiveFailures, 0) == 0) return; // no-op if already zero
            lock (_windowLock) { _firstFailureUtc = null; }
        }

        /// <summary>
        /// Record a 401/403 from the given <paramref name="operation"/>. Emits <see cref="ThresholdExceeded"/>
        /// the first time either ceiling is crossed; subsequent calls after termination is armed are no-ops.
        /// </summary>
        public void RecordFailure(int statusCode, string operation)
        {
            if (Volatile.Read(ref _thresholdFired) == 1) return;

            var count = Interlocked.Increment(ref _consecutiveFailures);
            DateTime now = _clock.UtcNow;
            DateTime firstFailureAt;

            lock (_windowLock)
            {
                if (_firstFailureUtc == null) _firstFailureUtc = now;
                firstFailureAt = _firstFailureUtc.Value;
            }

            bool countExceeded = _maxFailures > 0 && count >= _maxFailures;
            bool windowExceeded = _timeoutWindow.HasValue && (now - firstFailureAt) >= _timeoutWindow.Value;

            if (!countExceeded && !windowExceeded)
            {
                _logger.Warning($"AuthFailureTracker: consecutive auth failure #{count} (http {statusCode}, {operation}).");
                return;
            }

            if (Interlocked.Exchange(ref _thresholdFired, 1) == 1) return; // another thread won the race

            var reason = countExceeded
                ? $"consecutive auth failures reached limit ({count} >= {_maxFailures})"
                : $"auth-failure time window exceeded ({(now - firstFailureAt).TotalMinutes:F0}min >= {_timeoutWindow.Value.TotalMinutes:F0}min)";

            _logger.Error($"AuthFailureTracker: {reason} — signalling shutdown. Last operation: {operation}, statusCode={statusCode}.");

            try
            {
                ThresholdExceeded?.Invoke(this, new AuthFailureThresholdEventArgs(
                    consecutiveFailures: count,
                    firstFailureUtc: firstFailureAt,
                    lastOperation: operation,
                    lastStatusCode: statusCode,
                    reason: reason));
            }
            catch (Exception ex)
            {
                _logger.Warning($"AuthFailureTracker: ThresholdExceeded handler threw: {ex.Message}");
            }
        }
    }

    public sealed class AuthFailureThresholdEventArgs : EventArgs
    {
        public AuthFailureThresholdEventArgs(
            int consecutiveFailures,
            DateTime firstFailureUtc,
            string lastOperation,
            int lastStatusCode,
            string reason)
        {
            ConsecutiveFailures = consecutiveFailures;
            FirstFailureUtc = firstFailureUtc;
            LastOperation = lastOperation;
            LastStatusCode = lastStatusCode;
            Reason = reason;
        }

        public int ConsecutiveFailures { get; }
        public DateTime FirstFailureUtc { get; }
        public string LastOperation { get; }
        public int LastStatusCode { get; }
        public string Reason { get; }
    }
}
