using System;
using System.Threading;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.Core.Monitoring.Collectors
{
    /// <summary>
    /// Abstract base for interval-based event collectors.
    /// Handles Timer lifecycle (Start/Stop/Dispose) so subclasses only implement Collect().
    /// </summary>
    public abstract class CollectorBase : IDisposable
    {
        protected readonly string SessionId;
        protected readonly string TenantId;
        protected readonly Action<EnrollmentEvent> EmitEvent;
        protected readonly AgentLogger Logger;

        private readonly int _intervalSeconds;
        private Timer _timer;
        private bool _disposed;

        protected CollectorBase(
            string sessionId,
            string tenantId,
            Action<EnrollmentEvent> emitEvent,
            AgentLogger logger,
            int intervalSeconds)
        {
            SessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            TenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            EmitEvent = emitEvent ?? throw new ArgumentNullException(nameof(emitEvent));
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _intervalSeconds = intervalSeconds;
        }

        /// <summary>Starts the collection interval timer.</summary>
        public void Start()
        {
            if (_disposed) return;
            OnBeforeStart();
            var interval = TimeSpan.FromSeconds(_intervalSeconds);
            _timer = new Timer(_ => CollectSafe(), null, GetInitialDelay(), interval);
            Logger.Info($"[{GetType().Name}] Started (interval={_intervalSeconds}s)");
        }

        /// <summary>Stops the collection timer without disposing the collector.</summary>
        public void Stop()
        {
            _timer?.Dispose();
            _timer = null;
            Logger.Info($"[{GetType().Name}] Stopped");
            OnAfterStop();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }

        /// <summary>Override to perform collection. Called on each timer tick.</summary>
        protected abstract void Collect();

        /// <summary>
        /// Override to return the initial delay before the first tick.
        /// Default: one full interval (same as the repeat interval).
        /// </summary>
        protected virtual TimeSpan GetInitialDelay() => TimeSpan.FromSeconds(_intervalSeconds);

        /// <summary>Optional hook called before the timer starts (e.g. to initialise counters).</summary>
        protected virtual void OnBeforeStart() { }

        /// <summary>Optional hook called after the timer stops (e.g. to dispose counters).</summary>
        protected virtual void OnAfterStop() { }

        private void CollectSafe()
        {
            try { Collect(); }
            catch (Exception ex) { Logger.Error($"[{GetType().Name}] Collection error: {ex.Message}", ex); }
        }
    }
}
