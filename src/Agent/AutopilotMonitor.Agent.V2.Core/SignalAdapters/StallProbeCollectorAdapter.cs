#nullable enable
using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;

namespace AutopilotMonitor.Agent.V2.Core.SignalAdapters
{
    /// <summary>
    /// Adapter for <see cref="StallProbeCollector"/> → <see cref="DecisionSignalKind.EspTerminalFailure"/>.
    /// Plan §2.1a / §2.2.
    /// <para>
    /// StallProbe ist eine 60-Sekunden-Tick-basierte Anomalie-Erkennung mit 4 parallelen
    /// Scan-Quellen (Provisioning-Registry, Diagnostics-Registry, Event-Logs, AppWorkload.log).
    /// Die <c>EspFailureDetected</c>-Event ist der Terminal-Failure-Pfad — nur wenn alle
    /// Quellen konvergieren auf eine Terminal-Situation (<c>isTerminal=true</c> in
    /// ProbeResult). Adapter führt fire-once-Dedup; Source-Origin unterscheidet vom
    /// ShellCore / Provisioning ESP-Failure-Pfaden.
    /// </para>
    /// </summary>
    internal sealed class StallProbeCollectorAdapter : IDisposable
    {
        private readonly StallProbeCollector _collector;
        private readonly ISignalIngressSink _ingress;
        private readonly IClock _clock;
        private bool _failurePosted;

        public StallProbeCollectorAdapter(
            StallProbeCollector collector,
            ISignalIngressSink ingress,
            IClock clock)
        {
            _collector = collector ?? throw new ArgumentNullException(nameof(collector));
            _ingress = ingress ?? throw new ArgumentNullException(nameof(ingress));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));

            _collector.EspFailureDetected += OnEspFailure;
        }

        public void Dispose()
        {
            _collector.EspFailureDetected -= OnEspFailure;
        }

        private void OnEspFailure(object sender, string terminalReason) => EmitFailure(terminalReason);

        internal void TriggerEspFailureFromTest(string terminalReason) => EmitFailure(terminalReason);

        private void EmitFailure(string terminalReason)
        {
            if (_failurePosted) return;
            _failurePosted = true;

            var safeReason = string.IsNullOrEmpty(terminalReason) ? "unknown" : terminalReason!;

            _ingress.Post(
                kind: DecisionSignalKind.EspTerminalFailure,
                occurredAtUtc: _clock.UtcNow,
                sourceOrigin: "StallProbeCollector",
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: "stall-probe-collector-v1",
                    summary: $"Stall-probe classified session as terminal (reason={safeReason})",
                    derivationInputs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["detectionSource"] = "stall-probe 60s idle classifier (4 scan sources)",
                        ["terminalReason"] = safeReason,
                    }),
                payload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["failureType"] = safeReason,
                    ["detector"] = "stall-probe",
                });
        }
    }
}
