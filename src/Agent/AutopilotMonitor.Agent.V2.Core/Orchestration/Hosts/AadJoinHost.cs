#nullable enable
using System;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.DecisionCore.Engine;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    internal sealed class AadJoinHost : ICollectorHost
    {
        public string Name => "AadJoinWatcher";

        private readonly AadJoinWatcher _watcher;
        private readonly AadJoinWatcherAdapter _adapter;
        private readonly HybridLoginPendingDetector _hybridLoginPendingDetector;
        private int _disposed;

        public AadJoinHost(
            AgentLogger logger,
            ISignalIngressSink ingress,
            IClock clock,
            Action? onRealUserJoined = null)
        {
            _watcher = new AadJoinWatcher(logger);
            _adapter = new AadJoinWatcherAdapter(_watcher, ingress, clock, onRealUserJoined: onRealUserJoined);
            _hybridLoginPendingDetector = new HybridLoginPendingDetector(
                watcher: _watcher,
                post: new InformationalEventPost(ingress, clock),
                logger: logger);
        }

        public void Start() => _watcher.Start();
        public void Stop() => _watcher.Stop();

        /// <summary>
        /// Composition-root entry point for the Hybrid User-Driven completion-gap fix
        /// (2026-05-01). Only call when both prerequisites hold: a) the prior agent process
        /// was killed by an OS reboot (<c>previousExitType=reboot_kill</c>), b) the
        /// Autopilot profile is Hybrid AAD Join (<c>isHybridJoin=true</c>). The detector
        /// itself enforces single-shot semantics + cancel-on-real-user — repeated calls or
        /// late calls after a real user joined are safe no-ops.
        /// </summary>
        public void ArmHybridLoginPendingDetector() => _hybridLoginPendingDetector.Arm();

        // Test seam — exposes the detector for unit tests in the V2.Core.Tests project.
        internal HybridLoginPendingDetector HybridLoginPendingDetectorForTest => _hybridLoginPendingDetector;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { _hybridLoginPendingDetector.Dispose(); } catch { }
            try { _adapter.Dispose(); } catch { }
            try { _watcher.Dispose(); } catch { }
        }
    }
}
