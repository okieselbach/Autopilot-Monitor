#nullable enable
using System;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.DecisionCore.Engine;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    internal sealed class EspAndHelloHost : ICollectorHost
    {
        public string Name => "EspAndHelloTracker";

        private readonly EspAndHelloTracker _tracker;
        private readonly EspAndHelloTrackerAdapter _adapter;
        private int _disposed;

        public EspAndHelloHost(
            string sessionId,
            string tenantId,
            AgentLogger logger,
            ISignalIngressSink ingress,
            IClock clock,
            int helloWaitTimeoutSeconds,
            bool modernDeploymentWatcherEnabled,
            int modernDeploymentLogLevelMax,
            bool modernDeploymentBackfillEnabled,
            int modernDeploymentBackfillLookbackMinutes,
            int[]? modernDeploymentHarmlessEventIds,
            string stateDirectory)
        {
            if (ingress == null) throw new ArgumentNullException(nameof(ingress));
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            var post = new InformationalEventPost(ingress, clock);
            _tracker = new EspAndHelloTracker(
                sessionId: sessionId,
                tenantId: tenantId,
                post: post,
                logger: logger,
                helloWaitTimeoutSeconds: helloWaitTimeoutSeconds,
                modernDeploymentWatcherEnabled: modernDeploymentWatcherEnabled,
                modernDeploymentLogLevelMax: modernDeploymentLogLevelMax,
                modernDeploymentBackfillEnabled: modernDeploymentBackfillEnabled,
                modernDeploymentBackfillLookbackMinutes: modernDeploymentBackfillLookbackMinutes,
                stateDirectory: stateDirectory,
                modernDeploymentHarmlessEventIds: modernDeploymentHarmlessEventIds);

            _adapter = new EspAndHelloTrackerAdapter(_tracker, ingress, clock);
        }

        public void Start() => _tracker.Start();
        public void Stop() => _tracker.Stop();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { _adapter.Dispose(); } catch { }
            try { _tracker.Dispose(); } catch { }
        }
    }
}
