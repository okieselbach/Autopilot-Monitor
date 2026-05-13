#nullable enable
using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.SignalAdapters
{
    /// <summary>
    /// Adapter for <see cref="ShellCoreTracker"/> — translates its 4 events into Decision-Signals.
    /// Plan §2.1a / §2.2.
    /// <para>
    /// Event mapping (Plan §2.2 DecisionSignalKind):
    /// <list type="bullet">
    ///   <item><c>FinalizingSetupPhaseTriggered</c> → <see cref="DecisionSignalKind.EspPhaseChanged"/> (phase=Finalizing)</item>
    ///   <item><c>WhiteGloveCompleted</c> → <see cref="DecisionSignalKind.WhiteGloveShellCoreSuccess"/></item>
    ///   <item><c>EspFailureDetected</c> → <see cref="DecisionSignalKind.EspTerminalFailure"/></item>
    ///   <item><c>EspExited</c> → <see cref="DecisionSignalKind.EspExiting"/></item>
    /// </list>
    /// </para>
    /// <para>
    /// Finalizing / WhiteGloveSuccess / EspFailure are fire-once (ShellCoreTracker has Dedup-Guards;
    /// the adapter adds per-kind flags so duplicate Subscribe-Calls or external re-entry can't
    /// double-post). <c>EspExiting</c> is intentionally NOT deduped at the adapter — Shell-Core
    /// 62407 fires at every ESP phase transition (Device→Account, Account→End), and the
    /// reducer (HandleEspExitingV1 + ShouldTransitionToAwaitingHello) decides which occurrence
    /// is the genuine post-AccountSetup exit that arms HelloSafety.
    /// </para>
    /// </summary>
    internal sealed class ShellCoreTrackerAdapter : IDisposable
    {
        private readonly ShellCoreTracker _tracker;
        private readonly ISignalIngressSink _ingress;
        private readonly IClock _clock;

        private bool _finalizingPosted;
        private bool _whiteGloveSuccessPosted;
        private bool _espFailurePosted;

        public ShellCoreTrackerAdapter(
            ShellCoreTracker tracker,
            ISignalIngressSink ingress,
            IClock clock)
        {
            _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
            _ingress = ingress ?? throw new ArgumentNullException(nameof(ingress));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));

            _tracker.FinalizingSetupPhaseTriggered += OnFinalizing;
            _tracker.WhiteGloveCompleted += OnWhiteGloveCompleted;
            _tracker.EspFailureDetected += OnEspFailure;
            _tracker.EspExited += OnEspExited;
        }

        public void Dispose()
        {
            _tracker.FinalizingSetupPhaseTriggered -= OnFinalizing;
            _tracker.WhiteGloveCompleted -= OnWhiteGloveCompleted;
            _tracker.EspFailureDetected -= OnEspFailure;
            _tracker.EspExited -= OnEspExited;
        }

        private void OnFinalizing(object sender, string reason) => EmitFinalizing(reason);
        private void OnWhiteGloveCompleted(object sender, EventArgs e) => EmitWhiteGloveSuccess();
        private void OnEspFailure(object sender, string failureType) => EmitEspFailure(failureType);
        private void OnEspExited(object sender, EspExitedEventArgs args) => EmitEspExiting(args.OccurredAtUtc);

        internal void TriggerFinalizingFromTest(string reason) => EmitFinalizing(reason);
        internal void TriggerWhiteGloveCompletedFromTest() => EmitWhiteGloveSuccess();
        internal void TriggerEspFailureFromTest(string failureType) => EmitEspFailure(failureType);
        internal void TriggerEspExitingFromTest(DateTime occurredAtUtc) => EmitEspExiting(occurredAtUtc);

        private void EmitFinalizing(string reason)
        {
            if (_finalizingPosted) return;
            _finalizingPosted = true;

            _ingress.Post(
                kind: DecisionSignalKind.EspPhaseChanged,
                occurredAtUtc: _clock.UtcNow,
                sourceOrigin: "ShellCoreTracker",
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: "shell-core-tracker-v1",
                    summary: "ESP Finalizing phase triggered (Shell-Core 62404/62407)",
                    derivationInputs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["eventSource"] = "Microsoft-Windows-Shell-Core",
                        ["phaseReason"] = reason ?? string.Empty,
                    }),
                payload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [SignalPayloadKeys.EspPhase] = EnrollmentPhase.FinalizingSetup.ToString(),
                    ["reason"] = reason ?? string.Empty,
                });
        }

        private void EmitWhiteGloveSuccess()
        {
            if (_whiteGloveSuccessPosted) return;
            _whiteGloveSuccessPosted = true;

            _ingress.Post(
                kind: DecisionSignalKind.WhiteGloveShellCoreSuccess,
                occurredAtUtc: _clock.UtcNow,
                sourceOrigin: "ShellCoreTracker",
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: "shell-core-tracker-v1",
                    summary: "WhiteGlove sealing success observed (Shell-Core)",
                    derivationInputs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["eventSource"] = "Microsoft-Windows-Shell-Core",
                        ["detection"] = "WhiteGloveCompleted event",
                    }));
        }

        private void EmitEspFailure(string failureType)
        {
            if (_espFailurePosted) return;
            _espFailurePosted = true;

            var safeFailureType = string.IsNullOrEmpty(failureType) ? "unknown" : failureType!;

            _ingress.Post(
                kind: DecisionSignalKind.EspTerminalFailure,
                occurredAtUtc: _clock.UtcNow,
                sourceOrigin: "ShellCoreTracker",
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: "shell-core-tracker-v1",
                    summary: $"ESP terminal failure detected (type={safeFailureType})",
                    derivationInputs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["eventSource"] = "Microsoft-Windows-Shell-Core",
                        ["failureType"] = safeFailureType,
                    }),
                payload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["failureType"] = safeFailureType,
                });
        }

        // OccurredAtUtc comes from the tracker (live = log time, backfill = record.TimeCreated)
        // rather than _clock.UtcNow so HandleEspExitingV1 can floor HelloSafety at the actual
        // ESP-exit moment via EffectiveDeadlineBase, not at wall-clock-now on a backfilled run.
        private void EmitEspExiting(DateTime occurredAtUtc)
        {
            _ingress.Post(
                kind: DecisionSignalKind.EspExiting,
                occurredAtUtc: occurredAtUtc,
                sourceOrigin: "ShellCoreTracker",
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: "shell-core-tracker-v1",
                    summary: "ESP exiting (Shell-Core 62407 OOBE_ESP*Exiting)",
                    derivationInputs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["eventSource"] = "Microsoft-Windows-Shell-Core",
                        ["eventId"] = "62407",
                    }));
        }
    }
}
