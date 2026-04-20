using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.DecisionCore.Engine
{
    // Lifecycle + cross-scenario shared handlers. Plan §2.5 partial-class layout.
    public sealed partial class DecisionEngine
    {
        /// <summary>
        /// Handle <see cref="DecisionSignalKind.SessionStarted"/>. Plan §2.7.
        /// <para>
        /// For a fresh session this is the first signal and the state already equals
        /// <see cref="DecisionState.CreateInitial(string, string)"/>. The handler still runs
        /// through the pipeline so the start is recorded as a journal transition (step 0) —
        /// this anchors the Inspector timeline.
        /// </para>
        /// <para>
        /// If the engine sees <c>SessionStarted</c> on a state whose stage is already
        /// something other than <see cref="SessionStage.SessionStarted"/>, we treat it as a
        /// defensive no-op (dead-end) rather than silently reinitializing — replay of a
        /// truncated log should fail visibly, not reset hard-won hypotheses.
        /// </para>
        /// </summary>
        private DecisionStep HandleSessionStartedV1(DecisionState state, DecisionSignal signal)
        {
            if (state.Stage != SessionStage.SessionStarted && state.StepIndex != 0)
            {
                var bookkeptDead = BumpStepBookkeeping(state, signal);
                return new DecisionStep(
                    bookkeptDead,
                    BuildDeadEndTransition(
                        state: state,
                        signal: signal,
                        nextStepIndex: bookkeptDead.StepIndex,
                        trigger: nameof(DecisionSignalKind.SessionStarted),
                        deadEndReason: $"session_started_in_active_state:{state.Stage}"),
                    Array.Empty<DecisionEffect>());
            }

            // Stage stays SessionStarted; this transition is the "we saw the start" anchor.
            var newState = state.ToBuilder()
                .WithStage(SessionStage.SessionStarted)
                .WithStepIndex(state.StepIndex + 1)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                .Build();

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: SessionStage.SessionStarted,
                nextStepIndex: newState.StepIndex,
                trigger: nameof(DecisionSignalKind.SessionStarted));

            return new DecisionStep(newState, transition, Array.Empty<DecisionEffect>());
        }

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.SessionRecovered"/>. Plan §2.7 sonder-case 1.
        /// <para>
        /// In M3.0 scope this is a generic bookkeeping handler. The White-Glove Part-1 →
        /// Part-2 post-reboot transition is implemented in <c>DecisionEngine.WhiteGlovePart2.cs</c>
        /// (M3.4) and takes precedence there when the prior stage was
        /// <see cref="SessionStage.WhiteGloveSealed"/>.
        /// </para>
        /// </summary>
        private DecisionStep HandleSessionRecoveredV1(DecisionState state, DecisionSignal signal)
        {
            // M3.4 will add WhiteGlove-Part-2 transition here. For M3.0 the handler is a
            // neutral "observed a restart" step — stage unchanged, StepIndex advanced.
            var newState = BumpStepBookkeeping(state, signal);
            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: state.Stage,
                nextStepIndex: newState.StepIndex,
                trigger: nameof(DecisionSignalKind.SessionRecovered));

            return new DecisionStep(newState, transition, Array.Empty<DecisionEffect>());
        }

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.SessionAborted"/>.
        /// <para>
        /// Emitted by the orchestrator, never by a collector. Stage transitions to
        /// <see cref="SessionStage.Failed"/> with <see cref="SessionOutcome.Aborted"/>.
        /// This is a terminal event; the orchestrator uses it to record admin-kill /
        /// override actions cleanly without going through the regular completion paths
        /// (plan §2.7 admin-action audit).
        /// </para>
        /// </summary>
        private DecisionStep HandleSessionAbortedV1(DecisionState state, DecisionSignal signal)
        {
            var nextStep = state.StepIndex + 1;
            var newState = state.ToBuilder()
                .WithStage(SessionStage.Failed)
                .WithOutcome(SessionOutcome.Aborted)
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                .ClearDeadlines()
                .Build();

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: SessionStage.Failed,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.SessionAborted));

            return new DecisionStep(newState, transition, Array.Empty<DecisionEffect>());
        }

        // ============================================================== shared helpers
        // Partial-class shared helpers used by Classic / SelfDeploying / WhiteGlove handlers
        // as they come online in M3.1+ live below. M3.0 establishes the skeleton; the bodies
        // grow with each sub-milestone.

        /// <summary>
        /// Determine the user-visible enrollment phase implied by an ESP phase-change signal.
        /// Plan §2.3 phase-fact mapping. Populated in M3.1 as Classic handlers come online.
        /// </summary>
        internal static EnrollmentPhase MapEspPhaseToEnrollmentPhase(string rawPhase)
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
