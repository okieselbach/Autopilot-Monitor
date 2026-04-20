#nullable enable
using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;

namespace AutopilotMonitor.Agent.V2.Core.SignalAdapters
{
    /// <summary>
    /// Adapter for <see cref="ImeLogTracker"/> → mehrere DecisionSignalKinds.
    /// Plan §2.1a / §2.2.
    /// <para>
    /// ImeLogTracker nutzt Action-Property-Callbacks (nicht Events). Adapter <b>ersetzt</b>
    /// diese Action-Props — Orchestrator darf die nicht parallel belegen. Dispose restauriert
    /// die vorherigen Action-Werte (nur für Clean-Shutdown relevant; normalerweise ist der
    /// Tracker selbst kurz vor Dispose).
    /// </para>
    /// <para>
    /// Mapping:
    /// <list type="bullet">
    ///   <item><c>OnEspPhaseChanged(phase)</c> → <see cref="DecisionSignalKind.EspPhaseChanged"/>
    ///     (dedup per distinct phase value — Reducer Plan §2.1a idempotenz-Anforderung).</item>
    ///   <item><c>OnUserSessionCompleted()</c> → <see cref="DecisionSignalKind.ImeUserSessionCompleted"/> (fire-once).</item>
    ///   <item><c>OnAppStateChanged(app, old, new)</c> → <see cref="DecisionSignalKind.AppInstallCompleted"/>
    ///     (state=Installed/Skipped/Postponed) oder <see cref="DecisionSignalKind.AppInstallFailed"/> (state=Error).
    ///     Dedup pro (AppId, terminal-state-tuple).</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Nicht Teil von M4.3.4:</b> <c>WhiteGloveSealingPatternDetected</c> — ImeLogTracker
    /// hat aktuell keinen öffentlichen Pattern-Match-Hook; Re-Mapping via LastMatchedPatternId
    /// braucht entweder Polling-Adapter oder Modifikation der Tracker-Action-Surface. Dokumentiert
    /// als M4-Follow-Up; Reducer behandelt es derzeit korrekt auch ohne diesen Signal-Typ (alle
    /// M3-Szenarien grün).
    /// </para>
    /// </summary>
    internal sealed class ImeLogTrackerAdapter : IDisposable
    {
        private readonly ImeLogTracker _tracker;
        private readonly ISignalIngressSink _ingress;
        private readonly IClock _clock;
        private readonly object _lock = new object();

        // Previous action-handlers — restored on Dispose so we don't leave a dead callback
        // hanging on the tracker.
        private readonly Action<string>? _prevOnEspPhaseChanged;
        private readonly Action? _prevOnUserSessionCompleted;
        private readonly Action<AppPackageState, AppInstallationState, AppInstallationState>? _prevOnAppStateChanged;

        // Our own delegate instances — stored once so Dispose can compare by reference.
        private readonly Action<string> _ourOnEspPhaseChanged;
        private readonly Action _ourOnUserSessionCompleted;
        private readonly Action<AppPackageState, AppInstallationState, AppInstallationState> _ourOnAppStateChanged;

        // Dedup state.
        private string? _lastEspPhase;
        private bool _userSessionCompletedPosted;
        private readonly HashSet<string> _appsAlreadyPostedTerminal = new HashSet<string>(StringComparer.Ordinal);

        public ImeLogTrackerAdapter(
            ImeLogTracker tracker,
            ISignalIngressSink ingress,
            IClock clock)
        {
            _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
            _ingress = ingress ?? throw new ArgumentNullException(nameof(ingress));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));

            // Chain-preserve: save any previously-wired handlers and invoke them from our
            // dispatcher. Lets the orchestrator co-wire diagnostic callbacks (e.g. logging)
            // without being blocked by the adapter.
            _prevOnEspPhaseChanged = _tracker.OnEspPhaseChanged;
            _prevOnUserSessionCompleted = _tracker.OnUserSessionCompleted;
            _prevOnAppStateChanged = _tracker.OnAppStateChanged;

            // Store our delegate instances once — implicit method-group conversions
            // create a new delegate each time, which would break Dispose's reference check.
            _ourOnEspPhaseChanged = OnEspPhaseChanged;
            _ourOnUserSessionCompleted = OnUserSessionCompleted;
            _ourOnAppStateChanged = OnAppStateChanged;

            _tracker.OnEspPhaseChanged = _ourOnEspPhaseChanged;
            _tracker.OnUserSessionCompleted = _ourOnUserSessionCompleted;
            _tracker.OnAppStateChanged = _ourOnAppStateChanged;
        }

        public void Dispose()
        {
            // Restore only if we're still the current handler; otherwise leave it alone
            // (someone else re-wired after us and owns it now).
            if (ReferenceEquals(_tracker.OnEspPhaseChanged, _ourOnEspPhaseChanged))
                _tracker.OnEspPhaseChanged = _prevOnEspPhaseChanged;
            if (ReferenceEquals(_tracker.OnUserSessionCompleted, _ourOnUserSessionCompleted))
                _tracker.OnUserSessionCompleted = _prevOnUserSessionCompleted;
            if (ReferenceEquals(_tracker.OnAppStateChanged, _ourOnAppStateChanged))
                _tracker.OnAppStateChanged = _prevOnAppStateChanged;
        }

        private void OnEspPhaseChanged(string phase)
        {
            _prevOnEspPhaseChanged?.Invoke(phase);
            EmitEspPhase(phase);
        }

        private void OnUserSessionCompleted()
        {
            _prevOnUserSessionCompleted?.Invoke();
            EmitUserSessionCompleted();
        }

        private void OnAppStateChanged(AppPackageState app, AppInstallationState oldState, AppInstallationState newState)
        {
            _prevOnAppStateChanged?.Invoke(app, oldState, newState);
            EmitAppState(app, oldState, newState);
        }

        internal void TriggerEspPhaseFromTest(string phase) => EmitEspPhase(phase);
        internal void TriggerUserSessionCompletedFromTest() => EmitUserSessionCompleted();
        internal void TriggerAppStateFromTest(AppPackageState app, AppInstallationState oldState, AppInstallationState newState) =>
            EmitAppState(app, oldState, newState);

        private void EmitEspPhase(string phase)
        {
            if (string.IsNullOrEmpty(phase)) return;

            lock (_lock)
            {
                if (string.Equals(_lastEspPhase, phase, StringComparison.Ordinal))
                {
                    return;  // Idempotent — phase unchanged.
                }
                _lastEspPhase = phase;
            }

            _ingress.Post(
                kind: DecisionSignalKind.EspPhaseChanged,
                occurredAtUtc: _clock.UtcNow,
                sourceOrigin: "ImeLogTracker",
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: "ime-log-tracker-v1",
                    summary: $"IME log phase transition → {phase}",
                    derivationInputs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["detectionSource"] = "IME log regex pattern",
                        ["phase"] = phase,
                    }),
                payload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [SignalPayloadKeys.EspPhase] = phase,
                });
        }

        private void EmitUserSessionCompleted()
        {
            lock (_lock)
            {
                if (_userSessionCompletedPosted) return;
                _userSessionCompletedPosted = true;
            }

            _ingress.Post(
                kind: DecisionSignalKind.ImeUserSessionCompleted,
                occurredAtUtc: _clock.UtcNow,
                sourceOrigin: "ImeLogTracker",
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: "ime-log-tracker-v1",
                    summary: "IME user session completed (all user-scope apps finished)",
                    derivationInputs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["detectionSource"] = "IME log pattern (UserSessionCompleted)",
                    }));
        }

        private void EmitAppState(AppPackageState app, AppInstallationState oldState, AppInstallationState newState)
        {
            if (app == null || string.IsNullOrEmpty(app.Id)) return;

            var terminalKind = ClassifyTerminalState(newState);
            if (terminalKind == null) return;   // Not a terminal state — skip.

            lock (_lock)
            {
                if (!_appsAlreadyPostedTerminal.Add(app.Id))
                {
                    return;  // Already posted terminal state for this app.
                }
            }

            _ingress.Post(
                kind: terminalKind.Value,
                occurredAtUtc: _clock.UtcNow,
                sourceOrigin: "ImeLogTracker",
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: "ime-log-tracker-v1",
                    summary: $"App {app.Id} → {newState}",
                    derivationInputs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["appId"] = app.Id,
                        ["appName"] = app.Name ?? string.Empty,
                        ["previousState"] = oldState.ToString(),
                        ["newState"] = newState.ToString(),
                    }),
                payload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["appId"] = app.Id,
                    ["newState"] = newState.ToString(),
                });
        }

        private static DecisionSignalKind? ClassifyTerminalState(AppInstallationState state)
        {
            switch (state)
            {
                case AppInstallationState.Installed:
                case AppInstallationState.Skipped:
                case AppInstallationState.Postponed:
                    return DecisionSignalKind.AppInstallCompleted;
                case AppInstallationState.Error:
                    return DecisionSignalKind.AppInstallFailed;
                default:
                    return null;
            }
        }
    }
}
