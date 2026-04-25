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
        /// Terminal only on the SelfDeploying / Device-Only path (no AccountSetup phase ever
        /// entered). On the Classic UserDriven-v1 path — where <see cref="DecisionState.AccountSetupEnteredUtc"/>
        /// is already set — this is just the ProvisioningStatusTracker marking DeviceSetupCategory
        /// resolved; completion on that path requires <see cref="DecisionSignalKind.HelloResolved"/>
        /// + <see cref="DecisionSignalKind.DesktopArrived"/> (handled by Classic.cs).
        /// </para>
        /// <para>
        /// Guarding on <see cref="DecisionState.AccountSetupEnteredUtc"/> prevents the regression
        /// observed in session e259c121-dc13-46d6-8e96-118f1da9845e (2026-04-22), where a late
        /// DeviceSetupProvisioningComplete (payload <c>deviceSetupResolved=unknown</c>) fired while
        /// Stage was <see cref="SessionStage.EspAccountSetup"/> and <see cref="DecisionState.HelloResolvedUtc"/>
        /// was still null — terminating the session before Hello even ran.
        /// </para>
        /// </summary>
        private DecisionStep HandleDeviceSetupProvisioningCompleteV1(DecisionState state, DecisionSignal signal)
        {
            var nextStep = state.StepIndex + 1;

            // Classic-path guard: AccountSetup already started → this signal is informational only.
            if (state.AccountSetupEnteredUtc != null)
            {
                var passthroughState = state.ToBuilder()
                    .WithStepIndex(nextStep)
                    .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                    .Build();

                var passthroughTransition = BuildTakenTransition(
                    before: state,
                    signal: signal,
                    toStage: state.Stage,
                    nextStepIndex: nextStep,
                    trigger: nameof(DecisionSignalKind.DeviceSetupProvisioningComplete));

                return new DecisionStep(passthroughState, passthroughTransition, Array.Empty<DecisionEffect>());
            }

            // SelfDeploying / Device-Only terminal path.
            // Codex follow-up #5: user-presence is now an observation-level check (late-AADJ
            // user flag + signal-level Hello/Desktop facts) — NOT the legacy AadJoinedWithUser
            // state field. Observations replace the legacy per-fact nullable bool.
            var hasUserPresence =
                (state.ScenarioObservations.AadUserJoinWithUserObserved != null
                 && state.ScenarioObservations.AadUserJoinWithUserObserved.Value) ||
                state.HelloResolvedUtc != null ||
                state.DesktopArrivedUtc != null;

            var deviceOnlyReason = hasUserPresence
                ? DeviceOnlyReasons.UserPresent
                : DeviceOnlyReasons.DeviceOnly;

            var updatedDeviceOnly = state.ClassifierOutcomes.DeviceOnlyDeployment.With(
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
            builder.ClassifierOutcomes = state.ClassifierOutcomes.WithDeviceOnlyDeployment(updatedDeviceOnly);

            // Strengthen Mode: observing DeviceSetupProvisioningComplete without preceding
            // AccountSetup strongly implies SelfDeploying-v1. Delegated to the updater.
            builder.ScenarioProfile = EnrollmentScenarioProfileUpdater.ApplyDeviceSetupProvisioningComplete(
                builder.ScenarioProfile, signal);

            var newState = builder.Build();

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: SessionStage.Completed,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.DeviceSetupProvisioningComplete));

            var effects = new[] { BuildEnrollmentCompleteEffect(newState, nameof(DecisionSignalKind.DeviceSetupProvisioningComplete)) };

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

            var strongDeviceOnly = state.ClassifierOutcomes.DeviceOnlyDeployment.With(
                level: HypothesisLevel.Strong,
                reason: DeviceOnlyReasons.DeviceOnly,
                score: 70,
                lastUpdatedUtc: signal.OccurredAtUtc);
            builder.ClassifierOutcomes = state.ClassifierOutcomes.WithDeviceOnlyDeployment(strongDeviceOnly);

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
