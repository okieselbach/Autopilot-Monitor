#nullable enable
using System;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Persistence;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Produktions-<see cref="IDecisionStepProcessor"/>. Plan §2.5 / §2.7 / L.1.
    /// <para>
    /// Sequence pro <see cref="ApplyStep"/>:
    /// <list type="number">
    ///   <item><see cref="IJournalWriter.Append"/> (Sofort-Flush, L.12) — wenn das wirft,
    ///         ist der Step nicht committed und der Failure-Counter zählt hoch.</item>
    ///   <item><see cref="IEffectRunner.RunAsync"/> synchron (sync-over-async auf
    ///         Ingress-Worker-Thread — kein SynchronizationContext, kein Deadlock-Risiko).
    ///         <see cref="EffectRunResult.SessionMustAbort"/> und Failures werden geloggt;
    ///         kein Throw, da EffectRunner bereits alle Failure-Klassen sauber mapped.</item>
    ///   <item><see cref="ISnapshotPersistence.Save"/> best-effort — Exception wird
    ///         geloggt, aber der Step ist trotzdem committed (Journal ist die Wahrheit).</item>
    ///   <item><see cref="CurrentState"/> wird auf <see cref="DecisionStep.NewState"/>
    ///         fortgesetzt; Failure-Counter reset.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Quarantine-Eskalation</b>: Journal-Failures werden gezählt. Nach
    /// <see cref="_quarantineThreshold"/> aufeinanderfolgenden Failures (default 3) ruft der
    /// Processor <see cref="IQuarantineSink.TriggerQuarantine"/> und wirft weiter. Der
    /// Ingress-Worker schluckt die Exception (SignalIngress.cs:300-307), aber die
    /// Quarantine-Senke hat dann den Alarm gespeichert → nächstes Agent-Start räumt auf.
    /// </para>
    /// <para>
    /// <b>Thread-Modell</b>: Einzelner Schreiber — Ingress-Worker-Thread. Keine Locks nötig.
    /// Lesen von <see cref="CurrentState"/> erfolgt aus demselben Thread direkt vor dem
    /// nächsten Reduce, ebenfalls unlockbar sicher.
    /// </para>
    /// </summary>
    public sealed class DecisionStepProcessor : IDecisionStepProcessor
    {
        /// <summary>Default — 3 Failures hintereinander → Quarantine.</summary>
        public const int DefaultQuarantineThreshold = 3;

        private readonly IJournalWriter _journal;
        private readonly IEffectRunner _effectRunner;
        private readonly ISnapshotPersistence _snapshot;
        private readonly IQuarantineSink _quarantineSink;
        private readonly AgentLogger _logger;
        private readonly int _quarantineThreshold;
        private readonly Action<DecisionState>? _onTerminalStageReached;

        private DecisionState _currentState;
        private int _consecutiveJournalFailures;
        private bool _quarantineTriggered;
        private bool _terminalNotified;

        public DecisionStepProcessor(
            DecisionState initialState,
            IJournalWriter journal,
            IEffectRunner effectRunner,
            ISnapshotPersistence snapshot,
            IQuarantineSink quarantineSink,
            AgentLogger logger,
            int quarantineThreshold = DefaultQuarantineThreshold,
            Action<DecisionState>? onTerminalStageReached = null)
        {
            if (quarantineThreshold <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(quarantineThreshold),
                    "Threshold must be > 0.");
            }

            _currentState = initialState ?? throw new ArgumentNullException(nameof(initialState));
            _journal = journal ?? throw new ArgumentNullException(nameof(journal));
            _effectRunner = effectRunner ?? throw new ArgumentNullException(nameof(effectRunner));
            _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            _quarantineSink = quarantineSink ?? throw new ArgumentNullException(nameof(quarantineSink));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _quarantineThreshold = quarantineThreshold;
            _onTerminalStageReached = onTerminalStageReached;

            // If recovery loaded a state that already sits on a terminal stage (e.g. a crash
            // after a success-path step but before Stop()), treat it as already-notified so we
            // do not re-fire the hook.
            _terminalNotified = initialState.Stage.IsTerminal();
        }

        public DecisionState CurrentState => _currentState;

        /// <summary>Test-Observability — anzahl aufeinanderfolgender Journal-Failures.</summary>
        public int ConsecutiveJournalFailureCount => _consecutiveJournalFailures;

        public void ApplyStep(DecisionStep step, DecisionSignal signal)
        {
            if (step == null) throw new ArgumentNullException(nameof(step));
            if (signal == null) throw new ArgumentNullException(nameof(signal));

            // 1) Journal-Append — einziger Fehler-Pfad der hart wirft + Quarantine eskaliert.
            try
            {
                _journal.Append(step.Transition);
            }
            catch (Exception ex)
            {
                _consecutiveJournalFailures++;
                _logger.Error(
                    $"DecisionStepProcessor: journal append failed (consecutive={_consecutiveJournalFailures}/{_quarantineThreshold}) " +
                    $"for signal ordinal={signal.SessionSignalOrdinal} kind={signal.Kind}.",
                    ex);

                if (_consecutiveJournalFailures >= _quarantineThreshold && !_quarantineTriggered)
                {
                    _quarantineTriggered = true;
                    TryTriggerQuarantine(
                        $"journal append failed {_consecutiveJournalFailures}x consecutively; " +
                        $"last signal ordinal={signal.SessionSignalOrdinal} kind={signal.Kind}");
                }

                throw;
            }

            // 2) EffectRunner — Async-Methode vom Ingress-Worker-Thread sync ausführen.
            // Kein SynchronizationContext auf einem reinen Background-Thread, deshalb
            // GetAwaiter().GetResult() hier deadlock-frei (L.1 Ingress-Worker-Thread).
            EffectRunResult effectResult;
            try
            {
                effectResult = _effectRunner
                    .RunAsync(step.Effects, step.NewState, signal.OccurredAtUtc, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                // EffectRunner.RunAsync ist vertraglich exception-frei (Plan §2.7b); ein
                // unerwartetes Throw loggen wir hart, lassen den Step aber committed (Journal
                // ist bereits persistiert). Weiterwerfen würde den Ingress-Worker stoppen.
                _logger.Error(
                    $"DecisionStepProcessor: effect runner threw unexpectedly for signal ordinal={signal.SessionSignalOrdinal}.",
                    ex);
                effectResult = EffectRunResult.Empty();
            }

            if (effectResult.SessionMustAbort)
            {
                _logger.Error(
                    $"DecisionStepProcessor: effect run signaled session abort " +
                    $"(reason='{effectResult.AbortReason}') for signal ordinal={signal.SessionSignalOrdinal}.");
            }
            else if (effectResult.Failures.Count > 0)
            {
                _logger.Warning(
                    $"DecisionStepProcessor: effect run completed with {effectResult.Failures.Count} non-fatal failure(s) " +
                    $"for signal ordinal={signal.SessionSignalOrdinal}.");
            }

            // 3) Snapshot — best-effort. Journal ist die Wahrheit, Snapshot ist Cache.
            try
            {
                _snapshot.Save(step.NewState);
            }
            catch (Exception ex)
            {
                _logger.Warning(
                    $"DecisionStepProcessor: snapshot save failed (transient, not escalated) " +
                    $"for signal ordinal={signal.SessionSignalOrdinal}: {ex.Message}");
            }

            // 4) State-Forward + Counter-Reset.
            _currentState = step.NewState;
            _consecutiveJournalFailures = 0;

            // 5) Terminal-stage detection (M4.6.β). Fires exactly once per agent run when the
            //    DecisionEngine transitions the session into a terminal SessionStage — the
            //    orchestrator turns this into the public EnrollmentTerminated event so peripheral
            //    consumers (CleanupService, SummaryDialog, DiagnosticsPackageService) can react
            //    without touching the kernel state machine.
            if (!_terminalNotified && _currentState.Stage.IsTerminal())
            {
                _terminalNotified = true;
                try { _onTerminalStageReached?.Invoke(_currentState); }
                catch (Exception ex)
                {
                    _logger.Error(
                        $"DecisionStepProcessor: onTerminalStageReached handler threw for stage {_currentState.Stage}.",
                        ex);
                }
            }
        }

        private void TryTriggerQuarantine(string reason)
        {
            try
            {
                _quarantineSink.TriggerQuarantine(reason);
            }
            catch (Exception ex)
            {
                _logger.Error("DecisionStepProcessor: quarantine sink threw unexpectedly; continuing.", ex);
            }
        }
    }
}
