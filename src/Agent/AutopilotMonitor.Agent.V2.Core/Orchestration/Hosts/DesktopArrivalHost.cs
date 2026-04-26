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

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { _adapter.Dispose(); } catch { }
            try { _detector.Dispose(); } catch { }
        }
    }
}
