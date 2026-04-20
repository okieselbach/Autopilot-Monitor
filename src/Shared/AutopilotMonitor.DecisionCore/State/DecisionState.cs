using System;
using System.Collections.Generic;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.DecisionCore.State
{
    /// <summary>
    /// Immutable snapshot of the engine-visible session state. Plan §2.3.
    /// <para>
    /// <b>Invariant</b>: all DTOs in DecisionCore are immutable value objects.
    /// "Change" is a new instance via <c>With…</c>-methods — no in-place mutation.
    /// Reducer contract: <c>(newState, transition, effects) = engine.Reduce(oldState, signal)</c>.
    /// </para>
    /// <para>
    /// Agent-process lifecycle flags (crash, admin actions, boot-time, heartbeat) live
    /// in <c>agent-lifecycle.json</c>, not here (L.11 separation).
    /// </para>
    /// </summary>
    public sealed class DecisionState
    {
        public const string CurrentSchemaVersion = "v2";

        public DecisionState(
            string sessionId,
            string tenantId,
            SessionStage stage,
            SessionOutcome? outcome,
            Hypothesis enrollmentType,
            Hypothesis whiteGloveSealing,
            Hypothesis whiteGlovePart2Completion,
            Hypothesis deviceOnlyDeployment,
            SignalFact<EnrollmentPhase>? currentEnrollmentPhase,
            SignalFact<DateTime>? deviceSetupEnteredUtc,
            SignalFact<DateTime>? accountSetupEnteredUtc,
            SignalFact<DateTime>? finalizingEnteredUtc,
            SignalFact<DateTime>? espFinalExitUtc,
            SignalFact<DateTime>? desktopArrivedUtc,
            SignalFact<DateTime>? helloResolvedUtc,
            SignalFact<DateTime>? systemRebootUtc,
            SignalFact<string>? helloOutcome,
            SignalFact<bool>? aadJoinedWithUser,
            SignalFact<string>? imeMatchedPatternId,
            SignalFact<bool>? shellCoreWhiteGloveSuccessSeen,
            SignalFact<bool>? whiteGloveSealingPatternSeen,
            IReadOnlyList<ActiveDeadline> deadlines,
            long lastAppliedSignalOrdinal,
            int stepIndex,
            string? schemaVersion = null)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                throw new ArgumentException("SessionId is mandatory.", nameof(sessionId));
            }

            if (string.IsNullOrEmpty(tenantId))
            {
                throw new ArgumentException("TenantId is mandatory.", nameof(tenantId));
            }

            SessionId = sessionId;
            TenantId = tenantId;
            Stage = stage;
            Outcome = outcome;
            EnrollmentType = enrollmentType ?? throw new ArgumentNullException(nameof(enrollmentType));
            WhiteGloveSealing = whiteGloveSealing ?? throw new ArgumentNullException(nameof(whiteGloveSealing));
            WhiteGlovePart2Completion = whiteGlovePart2Completion ?? throw new ArgumentNullException(nameof(whiteGlovePart2Completion));
            DeviceOnlyDeployment = deviceOnlyDeployment ?? throw new ArgumentNullException(nameof(deviceOnlyDeployment));
            CurrentEnrollmentPhase = currentEnrollmentPhase;
            DeviceSetupEnteredUtc = deviceSetupEnteredUtc;
            AccountSetupEnteredUtc = accountSetupEnteredUtc;
            FinalizingEnteredUtc = finalizingEnteredUtc;
            EspFinalExitUtc = espFinalExitUtc;
            DesktopArrivedUtc = desktopArrivedUtc;
            HelloResolvedUtc = helloResolvedUtc;
            SystemRebootUtc = systemRebootUtc;
            HelloOutcome = helloOutcome;
            AadJoinedWithUser = aadJoinedWithUser;
            ImeMatchedPatternId = imeMatchedPatternId;
            ShellCoreWhiteGloveSuccessSeen = shellCoreWhiteGloveSuccessSeen;
            WhiteGloveSealingPatternSeen = whiteGloveSealingPatternSeen;
            Deadlines = deadlines ?? throw new ArgumentNullException(nameof(deadlines));
            LastAppliedSignalOrdinal = lastAppliedSignalOrdinal;
            StepIndex = stepIndex;
            SchemaVersion = schemaVersion ?? CurrentSchemaVersion;
        }

        public string SessionId { get; }

        public string TenantId { get; }

        /// <summary>Engine-stage — what the reducer is currently waiting on.</summary>
        public SessionStage Stage { get; }

        /// <summary>Null when the session is non-terminal.</summary>
        public SessionOutcome? Outcome { get; }

        // --- Hypothesen ---
        public Hypothesis EnrollmentType { get; }

        public Hypothesis WhiteGloveSealing { get; }

        public Hypothesis WhiteGlovePart2Completion { get; }

        public Hypothesis DeviceOnlyDeployment { get; }

        // --- Enrollment-Phase (end-user reality, separate from Stage) ---
        public SignalFact<EnrollmentPhase>? CurrentEnrollmentPhase { get; }

        public SignalFact<DateTime>? DeviceSetupEnteredUtc { get; }

        public SignalFact<DateTime>? AccountSetupEnteredUtc { get; }

        public SignalFact<DateTime>? FinalizingEnteredUtc { get; }

        // --- Signal-induced facts (with source ordinal for evidence trace) ---
        public SignalFact<DateTime>? EspFinalExitUtc { get; }

        public SignalFact<DateTime>? DesktopArrivedUtc { get; }

        public SignalFact<DateTime>? HelloResolvedUtc { get; }

        public SignalFact<DateTime>? SystemRebootUtc { get; }

        public SignalFact<string>? HelloOutcome { get; }

        public SignalFact<bool>? AadJoinedWithUser { get; }

        public SignalFact<string>? ImeMatchedPatternId { get; }

        /// <summary>True once <see cref="Signals.DecisionSignalKind.WhiteGloveShellCoreSuccess"/> has fired. WG Part-1 indicator.</summary>
        public SignalFact<bool>? ShellCoreWhiteGloveSuccessSeen { get; }

        /// <summary>True once <see cref="Signals.DecisionSignalKind.WhiteGloveSealingPatternDetected"/> has fired. Signal-correlated WG path.</summary>
        public SignalFact<bool>? WhiteGloveSealingPatternSeen { get; }

        public IReadOnlyList<ActiveDeadline> Deadlines { get; }

        public long LastAppliedSignalOrdinal { get; }

        public int StepIndex { get; }

        public string SchemaVersion { get; }

        /// <summary>
        /// Produce a mutable builder pre-populated with this state's values.
        /// Reducer handlers call <c>state.ToBuilder().WithStage(...).Build()</c> to
        /// express immutable "copy with changes" ergonomically (plan §2.3 / L.3).
        /// </summary>
        public DecisionStateBuilder ToBuilder() => new DecisionStateBuilder(this);

        /// <summary>
        /// Construct the initial non-terminal state for a new session.
        /// Used by the reducer's <c>SessionStarted</c> handler in M3.
        /// </summary>
        public static DecisionState CreateInitial(string sessionId, string tenantId) =>
            new DecisionState(
                sessionId: sessionId,
                tenantId: tenantId,
                stage: SessionStage.SessionStarted,
                outcome: null,
                enrollmentType: Hypothesis.UnknownInstance,
                whiteGloveSealing: Hypothesis.UnknownInstance,
                whiteGlovePart2Completion: Hypothesis.UnknownInstance,
                deviceOnlyDeployment: Hypothesis.UnknownInstance,
                currentEnrollmentPhase: null,
                deviceSetupEnteredUtc: null,
                accountSetupEnteredUtc: null,
                finalizingEnteredUtc: null,
                espFinalExitUtc: null,
                desktopArrivedUtc: null,
                helloResolvedUtc: null,
                systemRebootUtc: null,
                helloOutcome: null,
                aadJoinedWithUser: null,
                imeMatchedPatternId: null,
                shellCoreWhiteGloveSuccessSeen: null,
                whiteGloveSealingPatternSeen: null,
                deadlines: Array.Empty<ActiveDeadline>(),
                lastAppliedSignalOrdinal: -1,
                stepIndex: 0);
    }
}
