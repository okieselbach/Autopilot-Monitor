#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.DecisionCore.Engine;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Single-rail refactor (plan §5.8) — wraps <see cref="Monitoring.Telemetry.DeviceInfo.DeviceInfoCollector"/>
    /// to deliver the V1 "Device Details" event surface (OS / hardware / TPM / BitLocker /
    /// AAD-Join / autopilot profile / ESP config / network / hardware spec, 14 event types).
    /// <para>
    /// Kernel host (not remote-config-gated). Fires <c>DeviceInfoCollector.CollectAll</c>
    /// on <see cref="Start"/> on a ThreadPool task so the orchestrator's critical path is not
    /// blocked by the underlying WMI / registry / networking probes. Exceptions from the task
    /// are swallowed and logged; a failure in any one sub-emit must not kill the agent.
    /// </para>
    /// <para>
    /// <b>Out of scope today</b>: the Legacy EnrollmentTracker also called
    /// <c>CollectAtEnrollmentStart</c> on first DeviceSetup phase detection (re-fetches AAD
    /// join / autopilot profile / ESP config / TPM once MDM enrollment has populated them)
    /// and <c>CollectAtEnd</c> at termination (re-fetches BitLocker + active NIC). Hooking
    /// those into the V2 signal timeline requires a reducer-driven subscription that does
    /// not exist yet. The basic <see cref="Start"/> path solves Parity Issue #2 for the
    /// dominant case; the phase-transition re-collections are a follow-up (plan §5.8 TODO).
    /// </para>
    /// </summary>
    internal sealed class DeviceInfoHost : ICollectorHost
    {
        public string Name => "DeviceInfoCollector";

        private readonly Monitoring.Telemetry.DeviceInfo.DeviceInfoCollector _collector;
        private readonly AgentLogger _logger;
        private int _disposed;

        public DeviceInfoHost(
            string sessionId,
            string tenantId,
            ISignalIngressSink ingress,
            IClock clock,
            AgentLogger logger)
        {
            if (ingress == null) throw new ArgumentNullException(nameof(ingress));
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            _logger = logger;
            var post = new InformationalEventPost(ingress, clock);
            // Plan §6 Fix 9 — the collector also posts an EspConfigDetected decision signal
            // when it reads the FirstSync SkipUser/SkipDevice registry values, so that Fix 8's
            // reducer guards have the SkipUserEsp/SkipDeviceEsp state facts to read.
            _collector = new Monitoring.Telemetry.DeviceInfo.DeviceInfoCollector(
                sessionId, tenantId, post, logger, ingress, clock);
        }

        public void Start()
        {
            // Fire-and-forget — WMI queries can take several seconds and must not block the
            // orchestrator's Start path. The collector emits its 13+ events into the ingress
            // pipe as each sub-collector completes.
            Task.Run(() =>
            {
                try { _collector.CollectAll(); }
                catch (Exception ex) { _logger.Warning($"DeviceInfoHost: CollectAll threw: {ex.Message}"); }
            });
            _logger.Info("DeviceInfoHost: CollectAll scheduled on background thread.");
        }

        public void Stop()
        {
            // No background worker to stop; the scheduled Task either completed or is still
            // running and will exit once it finishes emitting. Stop is a no-op by design.
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            // DeviceInfoCollector does not implement IDisposable — purely event-emitting.
        }
    }
}
