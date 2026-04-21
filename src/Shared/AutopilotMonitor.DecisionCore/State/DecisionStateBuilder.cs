using System;
using System.Collections.Generic;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.DecisionCore.State
{
    /// <summary>
    /// Mutable construction helper for producing a new immutable <see cref="DecisionState"/>.
    /// <para>
    /// Plan §2.3 / L.3 — <c>DecisionState</c> itself is immutable; reducer handlers use the
    /// builder to express "copy with changes" without typing all 20+ constructor arguments.
    /// Call <see cref="Build"/> to materialize a new <see cref="DecisionState"/>. The original
    /// state is never mutated.
    /// </para>
    /// </summary>
    public sealed class DecisionStateBuilder
    {
        public DecisionStateBuilder(DecisionState source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            SessionId = source.SessionId;
            TenantId = source.TenantId;
            Stage = source.Stage;
            Outcome = source.Outcome;
            EnrollmentType = source.EnrollmentType;
            WhiteGloveSealing = source.WhiteGloveSealing;
            WhiteGlovePart2Completion = source.WhiteGlovePart2Completion;
            DeviceOnlyDeployment = source.DeviceOnlyDeployment;
            CurrentEnrollmentPhase = source.CurrentEnrollmentPhase;
            DeviceSetupEnteredUtc = source.DeviceSetupEnteredUtc;
            AccountSetupEnteredUtc = source.AccountSetupEnteredUtc;
            FinalizingEnteredUtc = source.FinalizingEnteredUtc;
            EspFinalExitUtc = source.EspFinalExitUtc;
            DesktopArrivedUtc = source.DesktopArrivedUtc;
            HelloResolvedUtc = source.HelloResolvedUtc;
            SystemRebootUtc = source.SystemRebootUtc;
            HelloOutcome = source.HelloOutcome;
            AadJoinedWithUser = source.AadJoinedWithUser;
            ImeMatchedPatternId = source.ImeMatchedPatternId;
            ShellCoreWhiteGloveSuccessSeen = source.ShellCoreWhiteGloveSuccessSeen;
            WhiteGloveSealingPatternSeen = source.WhiteGloveSealingPatternSeen;
            UserAadSignInCompleteUtc = source.UserAadSignInCompleteUtc;
            HelloResolvedPart2Utc = source.HelloResolvedPart2Utc;
            DesktopArrivedPart2Utc = source.DesktopArrivedPart2Utc;
            AccountSetupCompletedPart2Utc = source.AccountSetupCompletedPart2Utc;
            Deadlines = new List<ActiveDeadline>(source.Deadlines);
            LastAppliedSignalOrdinal = source.LastAppliedSignalOrdinal;
            StepIndex = source.StepIndex;
            SchemaVersion = source.SchemaVersion;
        }

        public string SessionId { get; set; }
        public string TenantId { get; set; }
        public SessionStage Stage { get; set; }
        public SessionOutcome? Outcome { get; set; }
        public Hypothesis EnrollmentType { get; set; }
        public Hypothesis WhiteGloveSealing { get; set; }
        public Hypothesis WhiteGlovePart2Completion { get; set; }
        public Hypothesis DeviceOnlyDeployment { get; set; }
        public SignalFact<EnrollmentPhase>? CurrentEnrollmentPhase { get; set; }
        public SignalFact<DateTime>? DeviceSetupEnteredUtc { get; set; }
        public SignalFact<DateTime>? AccountSetupEnteredUtc { get; set; }
        public SignalFact<DateTime>? FinalizingEnteredUtc { get; set; }
        public SignalFact<DateTime>? EspFinalExitUtc { get; set; }
        public SignalFact<DateTime>? DesktopArrivedUtc { get; set; }
        public SignalFact<DateTime>? HelloResolvedUtc { get; set; }
        public SignalFact<DateTime>? SystemRebootUtc { get; set; }
        public SignalFact<string>? HelloOutcome { get; set; }
        public SignalFact<bool>? AadJoinedWithUser { get; set; }
        public SignalFact<string>? ImeMatchedPatternId { get; set; }
        public SignalFact<bool>? ShellCoreWhiteGloveSuccessSeen { get; set; }
        public SignalFact<bool>? WhiteGloveSealingPatternSeen { get; set; }
        public SignalFact<DateTime>? UserAadSignInCompleteUtc { get; set; }
        public SignalFact<DateTime>? HelloResolvedPart2Utc { get; set; }
        public SignalFact<DateTime>? DesktopArrivedPart2Utc { get; set; }
        public SignalFact<DateTime>? AccountSetupCompletedPart2Utc { get; set; }
        public List<ActiveDeadline> Deadlines { get; set; }
        public long LastAppliedSignalOrdinal { get; set; }
        public int StepIndex { get; set; }
        public string SchemaVersion { get; set; }

        // ---------- fluent helpers for the most common reducer operations ----------

        public DecisionStateBuilder WithStage(SessionStage stage) { Stage = stage; return this; }

        public DecisionStateBuilder WithOutcome(SessionOutcome? outcome) { Outcome = outcome; return this; }

        public DecisionStateBuilder WithStepIndex(int stepIndex) { StepIndex = stepIndex; return this; }

        public DecisionStateBuilder WithLastAppliedSignalOrdinal(long ordinal) { LastAppliedSignalOrdinal = ordinal; return this; }

        public DecisionStateBuilder WithCurrentEnrollmentPhase(EnrollmentPhase phase, long sourceSignalOrdinal)
        {
            CurrentEnrollmentPhase = new SignalFact<EnrollmentPhase>(phase, sourceSignalOrdinal);
            return this;
        }

        public DecisionStateBuilder AddDeadline(ActiveDeadline deadline)
        {
            if (deadline == null) throw new ArgumentNullException(nameof(deadline));
            // Replace-if-same-name semantic: deadlines identified by Name. Plan §2.6.
            for (int i = 0; i < Deadlines.Count; i++)
            {
                if (string.Equals(Deadlines[i].Name, deadline.Name, StringComparison.Ordinal))
                {
                    Deadlines[i] = deadline;
                    return this;
                }
            }
            Deadlines.Add(deadline);
            return this;
        }

        public DecisionStateBuilder CancelDeadline(string name)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            Deadlines.RemoveAll(d => string.Equals(d.Name, name, StringComparison.Ordinal));
            return this;
        }

        public DecisionStateBuilder ClearDeadlines() { Deadlines.Clear(); return this; }

        public DecisionState Build() =>
            new DecisionState(
                sessionId: SessionId,
                tenantId: TenantId,
                stage: Stage,
                outcome: Outcome,
                enrollmentType: EnrollmentType,
                whiteGloveSealing: WhiteGloveSealing,
                whiteGlovePart2Completion: WhiteGlovePart2Completion,
                deviceOnlyDeployment: DeviceOnlyDeployment,
                currentEnrollmentPhase: CurrentEnrollmentPhase,
                deviceSetupEnteredUtc: DeviceSetupEnteredUtc,
                accountSetupEnteredUtc: AccountSetupEnteredUtc,
                finalizingEnteredUtc: FinalizingEnteredUtc,
                espFinalExitUtc: EspFinalExitUtc,
                desktopArrivedUtc: DesktopArrivedUtc,
                helloResolvedUtc: HelloResolvedUtc,
                systemRebootUtc: SystemRebootUtc,
                helloOutcome: HelloOutcome,
                aadJoinedWithUser: AadJoinedWithUser,
                imeMatchedPatternId: ImeMatchedPatternId,
                shellCoreWhiteGloveSuccessSeen: ShellCoreWhiteGloveSuccessSeen,
                whiteGloveSealingPatternSeen: WhiteGloveSealingPatternSeen,
                userAadSignInCompleteUtc: UserAadSignInCompleteUtc,
                helloResolvedPart2Utc: HelloResolvedPart2Utc,
                desktopArrivedPart2Utc: DesktopArrivedPart2Utc,
                accountSetupCompletedPart2Utc: AccountSetupCompletedPart2Utc,
                deadlines: Deadlines.ToArray(),
                lastAppliedSignalOrdinal: LastAppliedSignalOrdinal,
                stepIndex: StepIndex,
                schemaVersion: SchemaVersion);
    }
}
