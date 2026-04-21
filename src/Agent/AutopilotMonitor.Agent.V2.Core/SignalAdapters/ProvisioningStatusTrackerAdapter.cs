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
    /// Adapter for <see cref="ProvisioningStatusTracker"/> → 2 DecisionSignalKinds.
    /// Plan §2.1a / §2.2.
    /// <para>
    /// Event mapping:
    /// <list type="bullet">
    ///   <item><c>DeviceSetupProvisioningComplete</c> → <see cref="DecisionSignalKind.DeviceSetupProvisioningComplete"/></item>
    ///   <item><c>EspFailureDetected</c> → <see cref="DecisionSignalKind.EspTerminalFailure"/></item>
    /// </list>
    /// </para>
    /// <para>
    /// Duplicate EspTerminalFailure-Signals aus ShellCoreTracker + ProvisioningStatusTracker
    /// sind erwartbar (zwei Detection-Quellen für dasselbe Outcome). Der Reducer handled das
    /// idempotent — erste Failure gewinnt und setzt Stage auf Failed; weitere sind dann Dead-End.
    /// Adapter führt nur lokale per-kind Dedup (innerhalb einer Tracker-Instanz).
    /// </para>
    /// </summary>
    internal sealed class ProvisioningStatusTrackerAdapter : IDisposable
    {
        private readonly ProvisioningStatusTracker _tracker;
        private readonly ISignalIngressSink _ingress;
        private readonly IClock _clock;

        private bool _deviceSetupCompletePosted;
        private bool _espFailurePosted;

        public ProvisioningStatusTrackerAdapter(
            ProvisioningStatusTracker tracker,
            ISignalIngressSink ingress,
            IClock clock)
        {
            _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
            _ingress = ingress ?? throw new ArgumentNullException(nameof(ingress));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));

            _tracker.DeviceSetupProvisioningComplete += OnDeviceSetupComplete;
            _tracker.EspFailureDetected += OnEspFailure;
        }

        public void Dispose()
        {
            _tracker.DeviceSetupProvisioningComplete -= OnDeviceSetupComplete;
            _tracker.EspFailureDetected -= OnEspFailure;
        }

        private void OnDeviceSetupComplete(object sender, EventArgs e) => EmitDeviceSetupComplete();
        private void OnEspFailure(object sender, string failureType) => EmitEspFailure(failureType);

        internal void TriggerDeviceSetupCompleteFromTest() => EmitDeviceSetupComplete();
        internal void TriggerEspFailureFromTest(string failureType) => EmitEspFailure(failureType);

        private void EmitDeviceSetupComplete()
        {
            if (_deviceSetupCompletePosted) return;
            _deviceSetupCompletePosted = true;

            var snapshot = _tracker.GetProvisioningCategorySnapshot();
            var deviceSetupResolved = snapshot.TryGetValue("DeviceSetup", out var dsState) && dsState.HasValue
                ? dsState.Value.ToString().ToLowerInvariant()
                : "unknown";

            _ingress.Post(
                kind: DecisionSignalKind.DeviceSetupProvisioningComplete,
                occurredAtUtc: _clock.UtcNow,
                sourceOrigin: "ProvisioningStatusTracker",
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: "provisioning-status-tracker-v1",
                    summary: "DeviceSetupCategory provisioning completed",
                    derivationInputs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["registrySource"] = @"HKLM\SOFTWARE\Microsoft\Provisioning\AutopilotSettings\DeviceSetupCategory.Status",
                        ["deviceSetupResolved"] = deviceSetupResolved,
                    }),
                payload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["deviceSetupResolved"] = deviceSetupResolved,
                });
        }

        private void EmitEspFailure(string failureType)
        {
            if (_espFailurePosted) return;
            _espFailurePosted = true;

            var safeFailureType = string.IsNullOrEmpty(failureType) ? "unknown" : failureType!;

            _ingress.Post(
                kind: DecisionSignalKind.EspTerminalFailure,
                occurredAtUtc: _clock.UtcNow,
                sourceOrigin: "ProvisioningStatusTracker",
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: "provisioning-status-tracker-v1",
                    summary: $"ESP terminal failure from provisioning registry (type={safeFailureType})",
                    derivationInputs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["registrySource"] = @"HKLM\SOFTWARE\Microsoft\Provisioning\AutopilotSettings\*.Status",
                        ["failureType"] = safeFailureType,
                    }),
                payload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["failureType"] = safeFailureType,
                });
        }
    }
}
