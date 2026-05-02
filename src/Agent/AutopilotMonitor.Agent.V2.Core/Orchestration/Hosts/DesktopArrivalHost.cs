#nullable enable
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.DecisionCore.Engine;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    internal sealed class DesktopArrivalHost : ICollectorHost
    {
        public string Name => "DesktopArrivalDetector";

        private readonly DesktopArrivalDetector _detector;
        private readonly DesktopArrivalDetectorAdapter _adapter;
        private int _disposed;

        public DesktopArrivalHost(AgentLogger logger, ISignalIngressSink ingress, IClock clock)
        {
            _detector = new DesktopArrivalDetector(logger);
            _adapter = new DesktopArrivalDetectorAdapter(_detector, ingress, clock);
        }

        public void Start() => _detector.Start();
        public void Stop() => _detector.Stop();

        /// <summary>
        /// Resets desktop-arrival tracking after a placeholder→real-user transition
        /// (Hybrid User-Driven completion-gap fix, 2026-05-01). Wired by the composition
        /// root through <see cref="AadJoinHost"/>'s <c>onRealUserJoined</c> callback so the
        /// fooUser desktop the detector observed during foo-OOBE is invalidated and polling
        /// restarts to detect the AD-user desktop after the Hybrid reboot.
        /// </summary>
        public void RequestResetForRealUserSwitch() => _detector.ResetForRealUserSwitch();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { _adapter.Dispose(); } catch { }
            try { _detector.Dispose(); } catch { }
        }
    }
}
