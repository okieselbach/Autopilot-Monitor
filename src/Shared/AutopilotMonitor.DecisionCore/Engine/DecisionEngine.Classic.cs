using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.DecisionCore.Engine
{
    // Classic UserDriven-v1 enrollment handlers. Plan §2.5 partial-class layout.
    public sealed partial class DecisionEngine
    {
        // Post-ESP Hello-safety grace period per plan §2.7.
        private static readonly TimeSpan s_helloSafetyWindow = TimeSpan.FromSeconds(300);

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.EspPhaseChanged"/>. Drives the
        /// <see cref="SessionStage.EspDeviceSetup"/> → <see cref="SessionStage.EspAccountSetup"/>
        /// progression and updates the user-visible <see cref="DecisionState.CurrentEnrollmentPhase"/>
        /// fact.
        /// </summary>
        private DecisionStep HandleEspPhaseChangedV1(DecisionState state, DecisionSignal signal)
        {
            var rawPhase = signal.Payload != null && signal.Payload.TryGetValue(SignalPayloadKeys.EspPhase, out var p)
                ? p
                : string.Empty;
            var enrollmentPhase = MapEspPhaseToEnrollmentPhase(rawPhase);

            var newStage = enrollmentPhase switch
            {
                EnrollmentPhase.DeviceSetup => SessionStage.EspDeviceSetup,
                EnrollmentPhase.AccountSetup => SessionStage.EspAccountSetup,
                EnrollmentPhase.FinalizingSetup => SessionStage.AwaitingHello,
                _ => state.Stage,
            };

            var nextStep = state.StepIndex + 1;
            var builder = state.ToBuilder()
                .WithStage(newStage)
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal);

            if (enrollmentPhase != EnrollmentPhase.Unknown)
            {
                builder.WithCurrentEnrollmentPhase(enrollmentPhase, signal.SessionSignalOrdinal);
            }

            if (enrollmentPhase == EnrollmentPhase.DeviceSetup && state.DeviceSetupEnteredUtc == null)
            {
                builder.DeviceSetupEnteredUtc = new SignalFact<DateTime>(signal.OccurredAtUtc, signal.SessionSignalOrdinal);
            }
            if (enrollmentPhase == EnrollmentPhase.AccountSetup && state.AccountSetupEnteredUtc == null)
            {
                builder.AccountSetupEnteredUtc = new SignalFact<DateTime>(signal.OccurredAtUtc, signal.SessionSignalOrdinal);
            }
            if (enrollmentPhase == EnrollmentPhase.FinalizingSetup && state.FinalizingEnteredUtc == null)
            {
                builder.FinalizingEnteredUtc = new SignalFact<DateTime>(signal.OccurredAtUtc, signal.SessionSignalOrdinal);
            }

            // Weak UserDriven-v1 hypothesis — seeing AccountSetup is the canonical tell.
            if (enrollmentPhase == EnrollmentPhase.AccountSetup &&
                state.EnrollmentType.Level == HypothesisLevel.Unknown)
            {
                builder.EnrollmentType = state.EnrollmentType.With(
                    level: HypothesisLevel.Weak,
                    reason: "account_setup_observed",
                    score: 40,
                    lastUpdatedUtc: signal.OccurredAtUtc);
            }

            var newState = builder.Build();

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: newStage,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.EspPhaseChanged));

            return new DecisionStep(newState, transition, Array.Empty<DecisionEffect>());
        }

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.EspExiting"/>. Records
        /// <see cref="DecisionState.EspFinalExitUtc"/>, transitions to
        /// <see cref="SessionStage.AwaitingHello"/>, and schedules the Hello-safety deadline
        /// so a hang on Hello doesn't strand the session.
        /// </summary>
        private DecisionStep HandleEspExitingV1(DecisionState state, DecisionSignal signal)
        {
            var nextStep = state.StepIndex + 1;
            var dueAtUtc = signal.OccurredAtUtc.Add(s_helloSafetyWindow);

            var helloSafety = new ActiveDeadline(
                name: DeadlineNames.HelloSafety,
                dueAtUtc: dueAtUtc,
                firesSignalKind: DecisionSignalKind.DeadlineFired,
                firesPayload: new Dictionary<string, string>
                {
                    [SignalPayloadKeys.Deadline] = DeadlineNames.HelloSafety,
                });

            var builder = state.ToBuilder()
                .WithStage(SessionStage.AwaitingHello)
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                .AddDeadline(helloSafety);
            builder.EspFinalExitUtc = new SignalFact<DateTime>(signal.OccurredAtUtc, signal.SessionSignalOrdinal);

            var newState = builder.Build();

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: SessionStage.AwaitingHello,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.EspExiting));

            var effects = new[]
            {
                new DecisionEffect(DecisionEffectKind.ScheduleDeadline, deadline: helloSafety),
            };

            return new DecisionStep(newState, transition, effects);
        }

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.HelloResolved"/>. Records the resolution fact
        /// and cancels the Hello-safety deadline. If Desktop has already arrived the session
        /// completes here; otherwise stage transitions to <see cref="SessionStage.AwaitingDesktop"/>.
        /// </summary>
        private DecisionStep HandleHelloResolvedV1(DecisionState state, DecisionSignal signal)
        {
            var outcome = signal.Payload != null && signal.Payload.TryGetValue(SignalPayloadKeys.HelloOutcome, out var o)
                ? o
                : "Success";

            var nextStep = state.StepIndex + 1;
            var builder = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                .CancelDeadline(DeadlineNames.HelloSafety);
            builder.HelloResolvedUtc = new SignalFact<DateTime>(signal.OccurredAtUtc, signal.SessionSignalOrdinal);
            builder.HelloOutcome = new SignalFact<string>(outcome, signal.SessionSignalOrdinal);

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
                trigger: nameof(DecisionSignalKind.HelloResolved));

            var effects = desktopAlreadyArrived
                ? new[] { BuildEnrollmentCompleteEffect() }
                : Array.Empty<DecisionEffect>();

            return new DecisionStep(newState, transition, effects);
        }

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.DesktopArrived"/>. Mirror of the Hello handler:
        /// records <see cref="DecisionState.DesktopArrivedUtc"/>, and if Hello has already
        /// resolved the session completes here.
        /// </summary>
        private DecisionStep HandleDesktopArrivedV1(DecisionState state, DecisionSignal signal)
        {
            var nextStep = state.StepIndex + 1;
            var builder = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal);
            builder.DesktopArrivedUtc = new SignalFact<DateTime>(signal.OccurredAtUtc, signal.SessionSignalOrdinal);

            var helloAlreadyResolved = state.HelloResolvedUtc != null;
            var toStage = helloAlreadyResolved ? SessionStage.Completed : state.Stage;
            builder.WithStage(toStage);

            if (helloAlreadyResolved)
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
                trigger: nameof(DecisionSignalKind.DesktopArrived));

            var effects = helloAlreadyResolved
                ? new[] { BuildEnrollmentCompleteEffect() }
                : Array.Empty<DecisionEffect>();

            return new DecisionStep(newState, transition, effects);
        }

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.ImeUserSessionCompleted"/>. Primarily a
        /// <c>EnrollmentType</c>-hypothesis strengthener (IME's user-session-complete pattern
        /// is a strong UserDriven-v1 indicator), and records the matched pattern id.
        /// </summary>
        private DecisionStep HandleImeUserSessionCompletedV1(DecisionState state, DecisionSignal signal)
        {
            var patternId = signal.Payload != null && signal.Payload.TryGetValue(SignalPayloadKeys.ImePatternId, out var pid)
                ? pid
                : null;

            var nextStep = state.StepIndex + 1;
            var builder = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal);

            if (!string.IsNullOrEmpty(patternId))
            {
                builder.ImeMatchedPatternId = new SignalFact<string>(patternId!, signal.SessionSignalOrdinal);
            }

            // Strengthen UserDriven-v1 hypothesis on user-session-complete.
            if (state.EnrollmentType.Level < HypothesisLevel.Strong)
            {
                builder.EnrollmentType = state.EnrollmentType.With(
                    level: HypothesisLevel.Strong,
                    reason: "ime_user_session_completed",
                    score: 75,
                    lastUpdatedUtc: signal.OccurredAtUtc);
            }

            var newState = builder.Build();

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: state.Stage,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.ImeUserSessionCompleted));

            return new DecisionStep(newState, transition, Array.Empty<DecisionEffect>());
        }

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.AadUserJoinedLate"/> — hypothesis-only update.
        /// <para>
        /// Per project memory <c>feedback_aad_joined_late_not_completion</c>: this signal is a
        /// classifier-state update ONLY — never a completion trigger. Stage is unchanged, no
        /// terminal event is emitted, but <see cref="DecisionState.AadJoinedWithUser"/> is set
        /// and the <see cref="DecisionState.EnrollmentType"/> hypothesis records the late-AADJ
        /// reason so downstream classifiers (WhiteGlovePart2CompletionClassifier in M3.4) can
        /// factor it in.
        /// </para>
        /// </summary>
        private DecisionStep HandleAadUserJoinedLateV1(DecisionState state, DecisionSignal signal)
        {
            var withUser = signal.Payload != null &&
                           signal.Payload.TryGetValue(SignalPayloadKeys.AadJoinedWithUser, out var raw) &&
                           bool.TryParse(raw, out var parsed) && parsed;

            var nextStep = state.StepIndex + 1;
            var builder = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal);
            builder.AadJoinedWithUser = new SignalFact<bool>(withUser, signal.SessionSignalOrdinal);

            // Annotate the EnrollmentType hypothesis reason without bumping level up.
            builder.EnrollmentType = state.EnrollmentType.With(
                reason: $"late_aadj_observed:withUser={withUser.ToString().ToLowerInvariant()}",
                lastUpdatedUtc: signal.OccurredAtUtc);

            var newState = builder.Build();

            // Explicit "taken but stage unchanged" transition — the Inspector should see the
            // hypothesis annotation but nobody should mistake it for a completion step.
            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: state.Stage,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.AadUserJoinedLate));

            return new DecisionStep(newState, transition, Array.Empty<DecisionEffect>());
        }

        // ============================================================== internal helpers

        private static DecisionEffect BuildEnrollmentCompleteEffect() =>
            new DecisionEffect(
                kind: DecisionEffectKind.EmitEventTimelineEntry,
                parameters: new Dictionary<string, string>
                {
                    ["eventType"] = "enrollment_complete",
                });
    }
}
