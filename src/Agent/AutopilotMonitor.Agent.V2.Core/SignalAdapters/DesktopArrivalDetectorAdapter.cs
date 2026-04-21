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
    /// Adapter for <see cref="DesktopArrivalDetector"/> → <see cref="DecisionSignalKind.DesktopArrived"/>
    /// (or <see cref="DecisionSignalKind.DesktopArrivedPart2"/> in post-reboot WhiteGlove-Part-2 mode).
    /// Plan §2.1a / §2.2.
    /// <para>
    /// Fire-once by design (the detector itself guards against duplicate fires; the adapter
    /// also guards defensively in case the detector is restarted).
    /// </para>
    /// </summary>
    internal sealed class DesktopArrivalDetectorAdapter : IDisposable
    {
        private readonly DesktopArrivalDetector _detector;
        private readonly ISignalIngressSink _ingress;
        private readonly IClock _clock;
        private readonly bool _part2Mode;
        private bool _fired;

        public DesktopArrivalDetectorAdapter(
            DesktopArrivalDetector detector,
            ISignalIngressSink ingress,
            IClock clock,
            bool part2Mode = false)
        {
            _detector = detector ?? throw new ArgumentNullException(nameof(detector));
            _ingress = ingress ?? throw new ArgumentNullException(nameof(ingress));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _part2Mode = part2Mode;

            _detector.DesktopArrived += OnDesktopArrived;
        }

        public void Dispose()
        {
            _detector.DesktopArrived -= OnDesktopArrived;
        }

        private void OnDesktopArrived(object sender, EventArgs e) => EmitInternal();

        /// <summary>Test hook — triggers the same emit-logic bypassing the event plumbing.</summary>
        internal void TriggerFromTest() => EmitInternal();

        private void EmitInternal()
        {
            if (_fired) return;
            _fired = true;

            var kind = _part2Mode ? DecisionSignalKind.DesktopArrivedPart2 : DecisionSignalKind.DesktopArrived;
            _ingress.Post(
                kind: kind,
                occurredAtUtc: _clock.UtcNow,
                sourceOrigin: "DesktopArrivalDetector",
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: "desktop-arrival-detector-v1",
                    summary: _part2Mode
                        ? "Desktop arrival observed (Part 2 — post-reboot user sign-in)"
                        : "Desktop arrival observed (explorer.exe under real user)",
                    derivationInputs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["detectionSource"] = "explorer.exe process poll",
                        ["part2Mode"] = _part2Mode ? "true" : "false",
                    }));
        }
    }
}
