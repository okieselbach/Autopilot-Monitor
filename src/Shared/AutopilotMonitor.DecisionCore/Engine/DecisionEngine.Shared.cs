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
            // Plan §2.7 sonder-case 1: WhiteGlove Part 1 -> Reboot -> Part 2.
            // If the recovered session was sealed, transition into the Part 2 awaiting-user
            // stage and arm the 24h safety deadline. See DecisionEngine.WhiteGlovePart2.cs.
            if (state.Stage == SessionStage.WhiteGloveSealed)
            {
                return HandleWhiteGlovePart1To2Bridge(state, signal);
            }

            // Otherwise the recovered state is already mid-flight elsewhere; the handler is
            // a neutral "observed a restart" step — stage unchanged, bookkeeping advanced.
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
        /// Handle <see cref="DecisionSignalKind.DeadlineFired"/>. Plan §2.6.
        /// <para>
        /// The payload carries <see cref="SignalPayloadKeys.Deadline"/> = the deadline name
        /// (from <see cref="DeadlineNames"/>). The handler removes the corresponding
        /// <see cref="ActiveDeadline"/> from state and dispatches to a deadline-specific body.
        /// Deadlines for stages that don't yet exist in this sub-milestone (e.g. Part-2
        /// safety) land in the Unknown-Deadline path — they complete bookkeeping without
        /// changing state, which lets M3.0 replay logs that contain future deadline names.
        /// </para>
        /// </summary>
        private DecisionStep HandleDeadlineFiredV1(DecisionState state, DecisionSignal signal)
        {
            var deadlineName = signal.Payload != null && signal.Payload.TryGetValue(SignalPayloadKeys.Deadline, out var n)
                ? n
                : null;

            if (string.IsNullOrEmpty(deadlineName))
            {
                var bookkeptDead = BumpStepBookkeeping(state, signal);
                return new DecisionStep(
                    bookkeptDead,
                    BuildDeadEndTransition(
                        state: state,
                        signal: signal,
                        nextStepIndex: bookkeptDead.StepIndex,
                        trigger: nameof(DecisionSignalKind.DeadlineFired),
                        deadEndReason: "deadline_fired_without_name"),
                    Array.Empty<DecisionEffect>());
            }

            switch (deadlineName)
            {
                case DeadlineNames.HelloSafety:
                    return HandleHelloSafetyDeadlineFired(state, signal);
                case DeadlineNames.DeviceOnlyEspDetection:
                    return HandleDeviceOnlyEspDetectionDeadlineFired(state, signal);
                case DeadlineNames.ClassifierTick:
                    return HandleClassifierTickDeadlineFired(state, signal);
                case DeadlineNames.WhiteGlovePart2Safety:
                    return HandleWhiteGlovePart2SafetyDeadlineFired(state, signal);
                default:
                    // Deadline name not recognized in this sub-milestone. Cancel it from state
                    // and record a neutral taken transition — M3.3+ adds handlers for
                    // ClassifierTick, M3.4 for WhiteGlovePart2Safety, etc.
                    var nextStepIgnored = state.StepIndex + 1;
                    var cancelled = state.ToBuilder()
                        .WithStepIndex(nextStepIgnored)
                        .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                        .CancelDeadline(deadlineName!)
                        .Build();
                    var transitionIgnored = BuildTakenTransition(
                        before: state,
                        signal: signal,
                        toStage: state.Stage,
                        nextStepIndex: nextStepIgnored,
                        trigger: $"DeadlineFired:{deadlineName}");
                    return new DecisionStep(cancelled, transitionIgnored, Array.Empty<DecisionEffect>());
            }
        }

        /// <summary>
        /// Hello-safety deadline fired: the post-ESP grace window expired without a
        /// <see cref="DecisionSignalKind.HelloResolved"/>. Treat as a Hello timeout — the
        /// session completes with <see cref="DecisionState.HelloOutcome"/>=<c>Timeout</c>
        /// if Desktop has also arrived; otherwise we stay in <see cref="SessionStage.AwaitingDesktop"/>
        /// and the downstream <c>DesktopArrived</c> handler completes the session.
        /// </summary>
        private DecisionStep HandleHelloSafetyDeadlineFired(DecisionState state, DecisionSignal signal)
        {
            var nextStep = state.StepIndex + 1;
            var builder = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                .CancelDeadline(DeadlineNames.HelloSafety);

            // If Hello already resolved before the deadline fired (race), the fact is already
            // set; don't overwrite it. Otherwise record the synthetic timeout.
            if (state.HelloResolvedUtc == null)
            {
                builder.HelloResolvedUtc = new SignalFact<DateTime>(signal.OccurredAtUtc, signal.SessionSignalOrdinal);
                builder.HelloOutcome = new SignalFact<string>("Timeout", signal.SessionSignalOrdinal);
            }

            var desktopAlreadyArrived = state.DesktopArrivedUtc != null;
            var toStage = desktopAlreadyArrived ? SessionStage.Completed : SessionStage.AwaitingDesktop;
            builder.WithStage(toStage);

            if (desktopAlreadyArrived)
            {
                builder.WithOutcome(SessionOutcome.EnrollmentComplete);
                builder.ClearDeadlines();
            }

            var newState = builder.Build();

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: toStage,
                nextStepIndex: nextStep,
                trigger: $"DeadlineFired:{DeadlineNames.HelloSafety}");

            DecisionEffect[] effects = desktopAlreadyArrived
                ? new[] { BuildEnrollmentCompleteEffect() }
                : Array.Empty<DecisionEffect>();

            return new DecisionStep(newState, transition, effects);
        }

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
