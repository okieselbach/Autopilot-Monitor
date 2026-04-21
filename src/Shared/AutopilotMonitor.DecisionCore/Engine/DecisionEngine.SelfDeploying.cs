using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.DecisionCore.Engine
{
    // SelfDeploying-v1 + Device-Only handlers. Plan §2.5 partial-class layout.
    public sealed partial class DecisionEngine
    {
        // Plan §2.7: "5min timer after DeviceSetup; if no AccountSetup -> classified as device-only".
        internal static readonly TimeSpan s_deviceOnlyEspDetectionWindow = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Hypothesis-reason tokens for <see cref="DecisionState.DeviceOnlyDeployment"/>.
        /// Plan §2.3 values UserPresent / DeviceOnly / Ambiguous — expressed through the
        /// <see cref="Hypothesis.Reason"/> field so we do not need a new enum.
        /// </summary>
        internal static class DeviceOnlyReasons
        {
            public const string UserPresent = "user_present";
            public const string DeviceOnly = "device_only";
            public const string Ambiguous = "ambiguous";
        }

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.DeviceSetupProvisioningComplete"/>.
        /// <para>
        /// The canonical SelfDeploying / Device-Only terminal signal. Cancels the
        /// <see cref="DeadlineNames.DeviceOnlyEspDetection"/> deadline if still armed,
        /// finalizes the <see cref="DecisionState.DeviceOnlyDeployment"/> hypothesis based
        /// on whether any user-presence fact was observed, and transitions the session to
        /// <see cref="SessionStage.Completed"/> with <see cref="SessionOutcome.EnrollmentComplete"/>.
        /// </para>
        /// </summary>
        private DecisionStep HandleDeviceSetupProvisioningCompleteV1(DecisionState state, DecisionSignal signal)
        {
            var nextStep = state.StepIndex + 1;

            // Determine DeviceOnlyDeployment verdict.
            var hasUserPresence =
                (state.AadJoinedWithUser != null && state.AadJoinedWithUser.Value) ||
                state.HelloResolvedUtc != null ||
                state.DesktopArrivedUtc != null;

            var deviceOnlyReason = hasUserPresence
                ? DeviceOnlyReasons.UserPresent
                : DeviceOnlyReasons.DeviceOnly;

            var updatedHypothesis = state.DeviceOnlyDeployment.With(
                level: HypothesisLevel.Confirmed,
                reason: deviceOnlyReason,
                score: 100,
                lastUpdatedUtc: signal.OccurredAtUtc);

            var builder = state.ToBuilder()
                .WithStage(SessionStage.Completed)
                .WithOutcome(SessionOutcome.EnrollmentComplete)
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                .ClearDeadlines();
            builder.DeviceOnlyDeployment = updatedHypothesis;

            // Also strengthen EnrollmentType: observing DeviceSetupProvisioningComplete
            // without preceding AccountSetup strongly implies SelfDeploying-v1.
            if (state.AccountSetupEnteredUtc == null &&
                state.EnrollmentType.Level < HypothesisLevel.Strong)
            {
                builder.EnrollmentType = state.EnrollmentType.With(
                    level: HypothesisLevel.Strong,
                    reason: "selfdeploying_provisioning_complete",
                    score: 80,
                    lastUpdatedUtc: signal.OccurredAtUtc);
            }

            var newState = builder.Build();

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: SessionStage.Completed,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.DeviceSetupProvisioningComplete));

            var effects = new[] { BuildEnrollmentCompleteEffect() };

            return new DecisionStep(newState, transition, effects);
        }

        /// <summary>
        /// Called by the shared <c>DeadlineFired</c> router when
        /// <see cref="DeadlineNames.DeviceOnlyEspDetection"/> elapses without an AccountSetup
        /// phase having arrived. Confirms the <see cref="DecisionState.DeviceOnlyDeployment"/>
        /// hypothesis as DeviceOnly but does NOT complete the session — the terminal still
        /// needs an explicit <see cref="DecisionSignalKind.DeviceSetupProvisioningComplete"/>.
        /// </summary>
        private DecisionStep HandleDeviceOnlyEspDetectionDeadlineFired(DecisionState state, DecisionSignal signal)
        {
            var nextStep = state.StepIndex + 1;
            var builder = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                .CancelDeadline(DeadlineNames.DeviceOnlyEspDetection);

            builder.DeviceOnlyDeployment = state.DeviceOnlyDeployment.With(
                level: HypothesisLevel.Strong,
                reason: DeviceOnlyReasons.DeviceOnly,
                score: 70,
                lastUpdatedUtc: signal.OccurredAtUtc);

            var newState = builder.Build();

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: state.Stage,
                nextStepIndex: nextStep,
                trigger: $"DeadlineFired:{DeadlineNames.DeviceOnlyEspDetection}");

            return new DecisionStep(newState, transition, Array.Empty<DecisionEffect>());
        }

        // ============================================================== internal helpers

        /// <summary>
        /// Called from the Classic <see cref="HandleEspPhaseChangedV1"/> handler when
        /// <c>DeviceSetup</c> is observed for the first time — arms the
        /// <see cref="DeadlineNames.DeviceOnlyEspDetection"/> deadline. The Classic handler
        /// adds the deadline effect to its own effect list.
        /// </summary>
        internal ActiveDeadline BuildDeviceOnlyEspDetectionDeadline(DateTime fromUtc) =>
            new ActiveDeadline(
                name: DeadlineNames.DeviceOnlyEspDetection,
                dueAtUtc: fromUtc.Add(s_deviceOnlyEspDetectionWindow),
                firesSignalKind: DecisionSignalKind.DeadlineFired,
                firesPayload: new Dictionary<string, string>
                {
                    [SignalPayloadKeys.Deadline] = DeadlineNames.DeviceOnlyEspDetection,
                });
    }
}
