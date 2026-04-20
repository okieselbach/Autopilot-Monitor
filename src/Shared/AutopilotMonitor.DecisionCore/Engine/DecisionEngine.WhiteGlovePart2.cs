using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Classifiers;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.DecisionCore.Engine
{
    // WhiteGlove Part 2 (post-reboot user sign-in) handlers. Plan §2.5 / §M3.4.
    public sealed partial class DecisionEngine
    {
        // Plan §2.3: "Part-2-Safety-Timeout (z.B. 24h nach Reboot)" watches for the user
        // never signing in. Firing yields Outcome=EnrollmentFailed reason=part2_user_absent.
        internal static readonly TimeSpan s_whiteGlovePart2SafetyWindow = TimeSpan.FromHours(24);

        /// <summary>
        /// WhiteGlove Part 1 → Part 2 bridge. Called from <see cref="HandleSessionRecoveredV1"/>
        /// when the recovered state is <see cref="SessionStage.WhiteGloveSealed"/>. Transitions
        /// into <see cref="SessionStage.WhiteGloveAwaitingUserSignIn"/> and arms the Part-2
        /// safety deadline.
        /// </summary>
        private DecisionStep HandleWhiteGlovePart1To2Bridge(DecisionState state, DecisionSignal signal)
        {
            var nextStep = state.StepIndex + 1;
            var safety = new ActiveDeadline(
                name: DeadlineNames.WhiteGlovePart2Safety,
                dueAtUtc: signal.OccurredAtUtc.Add(s_whiteGlovePart2SafetyWindow),
                firesSignalKind: DecisionSignalKind.DeadlineFired,
                firesPayload: new Dictionary<string, string>
                {
                    [SignalPayloadKeys.Deadline] = DeadlineNames.WhiteGlovePart2Safety,
                });

            var builder = state.ToBuilder()
                .WithStage(SessionStage.WhiteGloveAwaitingUserSignIn)
                .WithOutcome(null) // leaving the Part-1 Sealed outcome aside until Part-2 concludes.
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                .AddDeadline(safety);
            builder.SystemRebootUtc = new SignalFact<DateTime>(signal.OccurredAtUtc, signal.SessionSignalOrdinal);

            var newState = builder.Build();

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: SessionStage.WhiteGloveAwaitingUserSignIn,
                nextStepIndex: nextStep,
                trigger: "SessionRecovered:WhiteGloveSealed->AwaitingUserSignIn");

            var effects = new[]
            {
                new DecisionEffect(DecisionEffectKind.ScheduleDeadline, deadline: safety),
            };

            return new DecisionStep(newState, transition, effects);
        }

        private DecisionStep HandleUserAadSignInCompleteV1(DecisionState state, DecisionSignal signal)
        {
            return UpdatePart2Fact(
                state,
                signal,
                applyFact: b => { b.UserAadSignInCompleteUtc = new SignalFact<DateTime>(signal.OccurredAtUtc, signal.SessionSignalOrdinal); },
                trigger: nameof(DecisionSignalKind.UserAadSignInComplete));
        }

        private DecisionStep HandleHelloResolvedPart2V1(DecisionState state, DecisionSignal signal)
        {
            return UpdatePart2Fact(
                state,
                signal,
                applyFact: b => { b.HelloResolvedPart2Utc = new SignalFact<DateTime>(signal.OccurredAtUtc, signal.SessionSignalOrdinal); },
                trigger: nameof(DecisionSignalKind.HelloResolvedPart2));
        }

        private DecisionStep HandleDesktopArrivedPart2V1(DecisionState state, DecisionSignal signal)
        {
            return UpdatePart2Fact(
                state,
                signal,
                applyFact: b => { b.DesktopArrivedPart2Utc = new SignalFact<DateTime>(signal.OccurredAtUtc, signal.SessionSignalOrdinal); },
                trigger: nameof(DecisionSignalKind.DesktopArrivedPart2));
        }

        private DecisionStep HandleAccountSetupCompletedPart2V1(DecisionState state, DecisionSignal signal)
        {
            return UpdatePart2Fact(
                state,
                signal,
                applyFact: b => { b.AccountSetupCompletedPart2Utc = new SignalFact<DateTime>(signal.OccurredAtUtc, signal.SessionSignalOrdinal); },
                trigger: nameof(DecisionSignalKind.AccountSetupCompletedPart2));
        }

        /// <summary>
        /// Part-2 safety deadline fired — user never signed in within 24 h. Terminates the
        /// session as <see cref="SessionOutcome.EnrollmentFailed"/> with reason
        /// <c>part2_user_absent</c>.
        /// </summary>
        private DecisionStep HandleWhiteGlovePart2SafetyDeadlineFired(DecisionState state, DecisionSignal signal)
        {
            var nextStep = state.StepIndex + 1;

            var builder = state.ToBuilder()
                .WithStage(SessionStage.Failed)
                .WithOutcome(SessionOutcome.EnrollmentFailed)
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                .ClearDeadlines();

            builder.WhiteGlovePart2Completion = state.WhiteGlovePart2Completion.With(
                level: HypothesisLevel.Rejected,
                reason: "part2_user_absent",
                score: 0,
                lastUpdatedUtc: signal.OccurredAtUtc);

            var newState = builder.Build();

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: SessionStage.Failed,
                nextStepIndex: nextStep,
                trigger: $"DeadlineFired:{DeadlineNames.WhiteGlovePart2Safety}");

            var effects = new[]
            {
                new DecisionEffect(
                    DecisionEffectKind.EmitEventTimelineEntry,
                    parameters: new Dictionary<string, string>
                    {
                        ["eventType"] = "enrollment_failed",
                        ["reason"] = "part2_user_absent",
                    }),
            };

            return new DecisionStep(newState, transition, effects);
        }

        // ============================================================ internal helpers

        private DecisionStep UpdatePart2Fact(
            DecisionState state,
            DecisionSignal signal,
            Action<DecisionStateBuilder> applyFact,
            string trigger)
        {
            var nextStep = state.StepIndex + 1;
            var builder = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal);

            applyFact(builder);

            // Part-2 only matters once we're in the WhiteGloveAwaitingUserSignIn stage;
            // if signals arrive on other stages, still record the fact but do not emit
            // a Part-2 classifier run.
            var emitClassifier =
                state.Stage == SessionStage.WhiteGloveAwaitingUserSignIn
                || state.Stage == SessionStage.WhiteGloveCompletedPart2;

            var stateAfterFact = builder.Build();

            var effects = emitClassifier
                ? new[] { BuildRunPart2ClassifierEffect(stateAfterFact) }
                : Array.Empty<DecisionEffect>();

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: stateAfterFact.Stage,
                nextStepIndex: nextStep,
                trigger: trigger);

            return new DecisionStep(stateAfterFact, transition, effects);
        }

        private static DecisionEffect BuildRunPart2ClassifierEffect(DecisionState state) =>
            new DecisionEffect(
                kind: DecisionEffectKind.RunClassifier,
                classifierId: WhiteGlovePart2CompletionClassifier.ClassifierId,
                classifierSnapshot: BuildWhiteGlovePart2CompletionSnapshot(state));

        internal static WhiteGlovePart2CompletionSnapshot BuildWhiteGlovePart2CompletionSnapshot(DecisionState state) =>
            new WhiteGlovePart2CompletionSnapshot(
                userAadSignInComplete: state.UserAadSignInCompleteUtc != null,
                helloResolvedPart2: state.HelloResolvedPart2Utc != null,
                desktopArrivedPart2: state.DesktopArrivedPart2Utc != null,
                accountSetupCompletedPart2: state.AccountSetupCompletedPart2Utc != null,
                currentEnrollmentPhase: state.CurrentEnrollmentPhase?.Value,
                systemRebootUtc: state.SystemRebootUtc?.Value);

        /// <summary>
        /// Apply a Part-2 classifier verdict to <see cref="DecisionState.WhiteGlovePart2Completion"/>.
        /// Called from the extended <see cref="HandleClassifierVerdictIssuedV1"/> router when
        /// the verdict's classifier id is the Part-2 completion classifier. On Confirmed,
        /// transitions to <see cref="SessionStage.WhiteGloveCompletedPart2"/> + Outcome
        /// <see cref="SessionOutcome.WhiteGlovePart2Complete"/> and emits the
        /// <c>whiteglove_part2_complete</c> event.
        /// </summary>
        private DecisionStep ApplyWhiteGlovePart2Verdict(
            DecisionState state,
            DecisionSignal signal,
            HypothesisLevel level,
            int score,
            string reason,
            string inputHash)
        {
            var nextStep = state.StepIndex + 1;
            var builder = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal);

            builder.WhiteGlovePart2Completion = state.WhiteGlovePart2Completion.With(
                level: level,
                reason: reason,
                score: score,
                lastUpdatedUtc: signal.OccurredAtUtc,
                lastClassifierVerdictId: inputHash);

            if (level == HypothesisLevel.Confirmed)
            {
                builder
                    .WithStage(SessionStage.WhiteGloveCompletedPart2)
                    .WithOutcome(SessionOutcome.WhiteGlovePart2Complete)
                    .ClearDeadlines();

                var sealedState = builder.Build();
                var sealedTransition = BuildTakenTransition(
                    before: state,
                    signal: signal,
                    toStage: SessionStage.WhiteGloveCompletedPart2,
                    nextStepIndex: nextStep,
                    trigger: $"ClassifierVerdictIssued:{WhiteGlovePart2CompletionClassifier.ClassifierId}:Confirmed");

                var effects = new[]
                {
                    new DecisionEffect(
                        DecisionEffectKind.EmitEventTimelineEntry,
                        parameters: new Dictionary<string, string> { ["eventType"] = "whiteglove_part2_complete" }),
                };

                return new DecisionStep(sealedState, sealedTransition, effects);
            }

            var newState = builder.Build();
            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: state.Stage,
                nextStepIndex: nextStep,
                trigger: $"ClassifierVerdictIssued:{WhiteGlovePart2CompletionClassifier.ClassifierId}:{level}");

            return new DecisionStep(newState, transition, Array.Empty<DecisionEffect>());
        }
    }
}
