#nullable enable
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
        private int _disposed;

        public AadJoinHost(AgentLogger logger, ISignalIngressSink ingress, IClock clock)
        {
            _watcher = new AadJoinWatcher(logger);
            _adapter = new AadJoinWatcherAdapter(_watcher, ingress, clock);
        }

        public void Start() => _watcher.Start();
        public void Stop() => _watcher.Stop();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { _adapter.Dispose(); } catch { }
            try { _watcher.Dispose(); } catch { }
        }
    }
}
