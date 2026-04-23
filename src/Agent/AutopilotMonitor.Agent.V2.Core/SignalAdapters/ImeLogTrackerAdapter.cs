#nullable enable
using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared.Models;
using SharedEventTypes = AutopilotMonitor.Shared.Constants.EventTypes;

namespace AutopilotMonitor.Agent.V2.Core.SignalAdapters
{
    /// <summary>
    /// Adapter for <see cref="ImeLogTracker"/> → mehrere DecisionSignalKinds.
    /// Plan §2.1a / §2.2 / §5.9 (single-rail PR #9 — V1 Parity Issue #3).
    /// <para>
    /// ImeLogTracker nutzt Action-Property-Callbacks (nicht Events). Adapter <b>ersetzt</b>
    /// diese Action-Props — Orchestrator darf die nicht parallel belegen. Dispose restauriert
    /// die vorherigen Action-Werte (nur für Clean-Shutdown relevant; normalerweise ist der
    /// Tracker selbst kurz vor Dispose).
    /// </para>
    /// <para>
    /// <b>Dual emission</b> (Plan §5.9): jede Callback postet (a) einen spezifischen
    /// <see cref="DecisionSignalKind"/> für Decision-Relevanz + (b) einen
    /// <see cref="DecisionSignalKind.InformationalEvent"/> für die Events-Timeline-UI. So bleibt
    /// die decision-Logik unverändert, während die V1-Event-Parity (app_install_started,
    /// app_download_started, download_progress, do_telemetry, script_completed, etc.) wieder
    /// hergestellt wird, ohne direkten <c>TelemetryEventEmitter.Emit</c>-Aufruf (Single-Rail
    /// Invariante 1).
    /// </para>
    /// <para>
    /// Mapping (DecisionSignal):
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
    /// <b>WhiteGloveSealingPatternDetected</b> (M4.4.4, Plan §4.x): fired via the newly added
    /// <c>ImeLogTracker.OnPatternMatched</c> hook. Orchestrator passes the configured set of
    /// WG-sealing-Pattern-IDs; this adapter fires the signal at most once per session when any
    /// of those IDs matches. Default empty collection = no emission (backwards-compatible with
    /// the pre-M4.4.4 M3 behavior).
    /// </para>
    /// </summary>
    internal sealed class ImeLogTrackerAdapter : IDisposable
    {
        private const string SourceLabel = "ImeLogTracker";

        private readonly ImeLogTracker _tracker;
        private readonly ISignalIngressSink _ingress;
        private readonly IClock _clock;
        private readonly InformationalEventPost _post;
        private readonly HashSet<string> _whiteGloveSealingPatternIds;
        private readonly object _lock = new object();

        // Previous action-handlers — restored on Dispose so we don't leave a dead callback
        // hanging on the tracker.
        private readonly Action<string>? _prevOnEspPhaseChanged;
        private readonly Action? _prevOnUserSessionCompleted;
        private readonly Action<AppPackageState, AppInstallationState, AppInstallationState>? _prevOnAppStateChanged;
        private readonly Action<string>? _prevOnPatternMatched;

        // Our own delegate instances — stored once so Dispose can compare by reference.
        private readonly Action<string> _ourOnEspPhaseChanged;
        private readonly Action _ourOnUserSessionCompleted;
        private readonly Action<AppPackageState, AppInstallationState, AppInstallationState> _ourOnAppStateChanged;
        private readonly Action<string> _ourOnPatternMatched;

        // Dedup state for DecisionSignals.
        private string? _lastEspPhase;
        private bool _userSessionCompletedPosted;
        private bool _sealingPatternPosted;
        private readonly HashSet<string> _appsAlreadyPostedTerminal = new HashSet<string>(StringComparer.Ordinal);

        public ImeLogTrackerAdapter(
            ImeLogTracker tracker,
            ISignalIngressSink ingress,
            IClock clock,
            IReadOnlyCollection<string>? whiteGloveSealingPatternIds = null)
        {
            _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
            _ingress = ingress ?? throw new ArgumentNullException(nameof(ingress));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _post = new InformationalEventPost(ingress, clock);

            _whiteGloveSealingPatternIds = whiteGloveSealingPatternIds == null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(whiteGloveSealingPatternIds, StringComparer.Ordinal);

            // Chain-preserve: save any previously-wired handlers and invoke them from our
            // dispatcher. Lets the orchestrator co-wire diagnostic callbacks (e.g. logging)
            // without being blocked by the adapter.
            _prevOnEspPhaseChanged = _tracker.OnEspPhaseChanged;
            _prevOnUserSessionCompleted = _tracker.OnUserSessionCompleted;
            _prevOnAppStateChanged = _tracker.OnAppStateChanged;
            _prevOnPatternMatched = _tracker.OnPatternMatched;

            // Store our delegate instances once — implicit method-group conversions
            // create a new delegate each time, which would break Dispose's reference check.
            _ourOnEspPhaseChanged = OnEspPhaseChanged;
            _ourOnUserSessionCompleted = OnUserSessionCompleted;
            _ourOnAppStateChanged = OnAppStateChanged;
            _ourOnPatternMatched = OnPatternMatched;

            _tracker.OnEspPhaseChanged = _ourOnEspPhaseChanged;
            _tracker.OnUserSessionCompleted = _ourOnUserSessionCompleted;
            _tracker.OnAppStateChanged = _ourOnAppStateChanged;
            _tracker.OnPatternMatched = _ourOnPatternMatched;
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
            if (ReferenceEquals(_tracker.OnPatternMatched, _ourOnPatternMatched))
                _tracker.OnPatternMatched = _prevOnPatternMatched;
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

        private void OnPatternMatched(string patternId)
        {
            _prevOnPatternMatched?.Invoke(patternId);
            MaybeEmitWhiteGloveSealingPattern(patternId);
        }

        internal void TriggerEspPhaseFromTest(string phase) => EmitEspPhase(phase);
        internal void TriggerUserSessionCompletedFromTest() => EmitUserSessionCompleted();
        internal void TriggerAppStateFromTest(AppPackageState app, AppInstallationState oldState, AppInstallationState newState) =>
            EmitAppState(app, oldState, newState);
        internal void TriggerPatternMatchedFromTest(string patternId) => MaybeEmitWhiteGloveSealingPattern(patternId);

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

            var now = _clock.UtcNow;

            _ingress.Post(
                kind: DecisionSignalKind.EspPhaseChanged,
                occurredAtUtc: now,
                sourceOrigin: SourceLabel,
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

            // Parity with V1: also emit an `esp_phase_changed` InformationalEvent so the
            // Events-table / UI timeline gets the phase declaration. This is one of the two
            // event types allowed to carry a non-Unknown Phase per feedback_phase_strategy.
            var mappedPhase = MapEspPhaseToEnrollmentPhase(phase);
            var data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["espPhase"] = phase,
            };
            var patternId = _tracker.LastMatchedPatternId;
            if (!string.IsNullOrEmpty(patternId))
                data["patternId"] = patternId!;

            _post.Emit(
                eventType: SharedEventTypes.EspPhaseChanged,
                source: SourceLabel,
                message: $"ESP phase: {phase}",
                phase: mappedPhase == EnrollmentPhase.Unknown ? (EnrollmentPhase?)null : mappedPhase,
                data: data,
                occurredAtUtc: now);
        }

        private void EmitUserSessionCompleted()
        {
            lock (_lock)
            {
                if (_userSessionCompletedPosted) return;
                _userSessionCompletedPosted = true;
            }

            var now = _clock.UtcNow;

            _ingress.Post(
                kind: DecisionSignalKind.ImeUserSessionCompleted,
                occurredAtUtc: now,
                sourceOrigin: SourceLabel,
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: "ime-log-tracker-v1",
                    summary: "IME user session completed (all user-scope apps finished)",
                    derivationInputs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["detectionSource"] = "IME log pattern (UserSessionCompleted)",
                    }));

            var data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["detectedAt"] = now.ToString("o"),
            };
            var patternId = _tracker.LastMatchedPatternId;
            if (!string.IsNullOrEmpty(patternId))
                data["patternId"] = patternId!;

            _post.Emit(
                eventType: SharedEventTypes.ImeUserSessionCompleted,
                source: SourceLabel,
                message: "IME user session completed",
                data: data,
                occurredAtUtc: now);
        }

        private void EmitAppState(AppPackageState app, AppInstallationState oldState, AppInstallationState newState)
        {
            if (app == null || string.IsNullOrEmpty(app.Id)) return;

            var now = _clock.UtcNow;

            // (1) DecisionSignal — terminal states only, once per app.
            var terminalKind = ClassifyTerminalState(newState);
            if (terminalKind != null)
            {
                bool fireDecisionSignal;
                lock (_lock)
                {
                    fireDecisionSignal = _appsAlreadyPostedTerminal.Add(app.Id);
                }

                if (fireDecisionSignal)
                {
                    _ingress.Post(
                        kind: terminalKind.Value,
                        occurredAtUtc: now,
                        sourceOrigin: SourceLabel,
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
            }

            // (2) InformationalEvent — one per state transition matching V1's wire shape.
            var mapped = MapAppStateToEventType(oldState, newState);
            if (mapped == null) return;

            var (eventType, severity) = mapped.Value;
            _post.Emit(
                eventType: eventType,
                source: SourceLabel,
                message: BuildAppStateMessage(app, newState, eventType),
                severity: severity,
                data: BuildAppStatePayload(app, newState),
                occurredAtUtc: now);
        }

        private void MaybeEmitWhiteGloveSealingPattern(string patternId)
        {
            if (string.IsNullOrEmpty(patternId)) return;
            if (_whiteGloveSealingPatternIds.Count == 0) return;   // No configured IDs — no emit.
            if (!_whiteGloveSealingPatternIds.Contains(patternId)) return;

            lock (_lock)
            {
                if (_sealingPatternPosted) return;
                _sealingPatternPosted = true;
            }

            _ingress.Post(
                kind: DecisionSignalKind.WhiteGloveSealingPatternDetected,
                occurredAtUtc: _clock.UtcNow,
                sourceOrigin: SourceLabel,
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: "ime-log-tracker-v1",
                    summary: $"WG sealing pattern match → {patternId}",
                    derivationInputs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["detectionSource"] = "IME log regex pattern (WG-sealing set)",
                        [SignalPayloadKeys.ImePatternId] = patternId,
                    }),
                payload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [SignalPayloadKeys.ImePatternId] = patternId,
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

        /// <summary>
        /// Maps an IME app state-transition tuple to the V1-compatible event-type string +
        /// severity. Returns null when the transition is not user-visible in the timeline
        /// (e.g. Unknown → NotInstalled intermediate states).
        /// </summary>
        private static (string EventType, EventSeverity Severity)? MapAppStateToEventType(
            AppInstallationState oldState,
            AppInstallationState newState)
        {
            switch (newState)
            {
                case AppInstallationState.Downloading:
                    // First transition into Downloading → `app_download_started`; subsequent
                    // byte updates (old == new == Downloading) → `download_progress` (Debug).
                    return oldState == AppInstallationState.Downloading
                        ? (SharedEventTypes.DownloadProgress, EventSeverity.Debug)
                        : (SharedEventTypes.AppDownloadStarted, EventSeverity.Info);

                case AppInstallationState.Installing:
                case AppInstallationState.InProgress:
                    return (SharedEventTypes.AppInstallStart, EventSeverity.Info);

                case AppInstallationState.Installed:
                case AppInstallationState.Skipped:
                case AppInstallationState.Postponed:
                    return (SharedEventTypes.AppInstallComplete, EventSeverity.Info);

                case AppInstallationState.Error:
                    return (SharedEventTypes.AppInstallFailed, EventSeverity.Error);

                default:
                    return null;
            }
        }

        private static string BuildAppStateMessage(AppPackageState app, AppInstallationState newState, string eventType)
        {
            var label = string.IsNullOrEmpty(app.Name) ? app.Id : app.Name;
            if (string.Equals(eventType, SharedEventTypes.AppInstallFailed, StringComparison.Ordinal)
                && !string.IsNullOrEmpty(app.ErrorDetail))
            {
                return $"{label}: {app.ErrorDetail}";
            }
            if (string.Equals(eventType, SharedEventTypes.DownloadProgress, StringComparison.Ordinal))
            {
                return $"{label}: {(app.ProgressPercent ?? 0)}%";
            }
            return $"{label}: {newState}";
        }

        private static IReadOnlyDictionary<string, string> BuildAppStatePayload(
            AppPackageState app,
            AppInstallationState newState)
        {
            var data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["appId"] = app.Id,
                ["appName"] = app.Name ?? string.Empty,
                ["state"] = newState.ToString(),
                ["intent"] = app.Intent.ToString(),
                ["targeted"] = app.Targeted.ToString(),
                ["runAs"] = app.RunAs.ToString(),
                ["progressPercent"] = (app.ProgressPercent ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["bytesDownloaded"] = app.BytesDownloaded.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["bytesTotal"] = app.BytesTotal.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["isError"] = (newState == AppInstallationState.Error).ToString().ToLowerInvariant(),
                ["isCompleted"] = IsCompletedState(newState).ToString().ToLowerInvariant(),
            };

            if (!string.IsNullOrEmpty(app.AppVersion)) data["appVersion"] = app.AppVersion!;
            if (!string.IsNullOrEmpty(app.AppType)) data["appType"] = app.AppType!;
            if (app.AttemptNumber > 0) data["attemptNumber"] = app.AttemptNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(app.DetectionResult)) data["detectionResult"] = app.DetectionResult!;

            if (newState == AppInstallationState.Error)
            {
                if (!string.IsNullOrEmpty(app.ErrorPatternId)) data["errorPatternId"] = app.ErrorPatternId!;
                if (!string.IsNullOrEmpty(app.ErrorDetail)) data["errorDetail"] = app.ErrorDetail!;
                if (!string.IsNullOrEmpty(app.ErrorCode)) data["errorCode"] = app.ErrorCode!;
                if (!string.IsNullOrEmpty(app.ExitCode)) data["exitCode"] = app.ExitCode!;
                if (!string.IsNullOrEmpty(app.HResultFromWin32)) data["hresultFromWin32"] = app.HResultFromWin32!;
            }

            return data;
        }

        private static bool IsCompletedState(AppInstallationState state) =>
            state == AppInstallationState.Installed
            || state == AppInstallationState.Skipped
            || state == AppInstallationState.Postponed
            || state == AppInstallationState.Error;

        /// <summary>
        /// Mirrors <see cref="DecisionEngine.MapEspPhaseToEnrollmentPhase(string)"/> so the
        /// adapter does not depend on the engine's <c>internal</c> helper (kept here for build
        /// isolation; drift should be caught by an xUnit parity test in a later PR).
        /// </summary>
        private static EnrollmentPhase MapEspPhaseToEnrollmentPhase(string rawPhase)
        {
            if (string.IsNullOrEmpty(rawPhase)) return EnrollmentPhase.Unknown;
            return rawPhase switch
            {
                "DeviceSetup" => EnrollmentPhase.DeviceSetup,
                "AccountSetup" => EnrollmentPhase.AccountSetup,
                "FinalizingSetup" => EnrollmentPhase.FinalizingSetup,
                "Finalizing" => EnrollmentPhase.FinalizingSetup,
                "Complete" => EnrollmentPhase.Complete,
                _ => EnrollmentPhase.Unknown,
            };
        }
    }
}
