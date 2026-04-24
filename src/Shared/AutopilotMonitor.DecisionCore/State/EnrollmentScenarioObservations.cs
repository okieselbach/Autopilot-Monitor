namespace AutopilotMonitor.DecisionCore.State
{
    /// <summary>
    /// Raw per-signal observations that feed the WhiteGlove sealing classifier and downstream
    /// guards. Codex follow-up #5 — replaces the legacy per-flag <see cref="SignalFact{T}"/>
    /// fields (<c>ShellCoreWhiteGloveSuccessSeen</c>, <c>WhiteGloveSealingPatternSeen</c>,
    /// <c>AadJoinedWithUser</c>, <c>SkipUserEsp</c>, <c>SkipDeviceEsp</c>) with a single aggregate.
    /// These are **evidence**, not classification — the derived classification lives in
    /// <see cref="EnrollmentScenarioProfile"/>.
    /// <para>
    /// <b>Invariants</b>:
    /// <list type="bullet">
    ///   <item>Immutable; the <c>With…</c> methods return new instances.</item>
    ///   <item>Set-once semantics for Boolean flags: once observed, later identical signals are
    ///         no-ops (the first-sighting ordinal is preserved as evidence).</item>
    ///   <item><see cref="AadUserJoinWithUserObserved"/> is the late-AADJ user-presence flag
    ///         (payload <c>aadJoinedWithUser</c>) — NOT the <see cref="EnrollmentJoinMode"/>.
    ///         See <see cref="EnrollmentJoinMode"/> remarks.</item>
    ///   <item><see cref="SkipUserEsp"/> / <see cref="SkipDeviceEsp"/> are the raw half-facts
    ///         from <see cref="Signals.DecisionSignalKind.EspConfigDetected"/>. The derived
    ///         <see cref="EnrollmentScenarioProfile.EspConfig"/> is only set when BOTH halves
    ///         are observed (signals can arrive partial — first skipUser-only, later skipDevice).</item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class EnrollmentScenarioObservations
    {
        public static readonly EnrollmentScenarioObservations Empty = new EnrollmentScenarioObservations(
            shellCoreWhiteGloveSuccessSeen: null,
            whiteGloveSealingPatternSeen: null,
            aadUserJoinWithUserObserved: null,
            skipUserEsp: null,
            skipDeviceEsp: null);

        public EnrollmentScenarioObservations(
            SignalFact<bool>? shellCoreWhiteGloveSuccessSeen,
            SignalFact<bool>? whiteGloveSealingPatternSeen,
            SignalFact<bool>? aadUserJoinWithUserObserved,
            SignalFact<bool>? skipUserEsp,
            SignalFact<bool>? skipDeviceEsp)
        {
            ShellCoreWhiteGloveSuccessSeen = shellCoreWhiteGloveSuccessSeen;
            WhiteGloveSealingPatternSeen = whiteGloveSealingPatternSeen;
            AadUserJoinWithUserObserved = aadUserJoinWithUserObserved;
            SkipUserEsp = skipUserEsp;
            SkipDeviceEsp = skipDeviceEsp;
        }

        /// <summary>True once <see cref="Signals.DecisionSignalKind.WhiteGloveShellCoreSuccess"/> has fired.</summary>
        public SignalFact<bool>? ShellCoreWhiteGloveSuccessSeen { get; }

        /// <summary>True once <see cref="Signals.DecisionSignalKind.WhiteGloveSealingPatternDetected"/> has fired.</summary>
        public SignalFact<bool>? WhiteGloveSealingPatternSeen { get; }

        /// <summary>
        /// Payload-carrying observation from <see cref="Signals.DecisionSignalKind.AadUserJoinedLate"/>.
        /// <c>true</c> = late AADJ observed with a user-side principal (hard-excluder for
        /// the WhiteGlove classifier); <c>false</c> = late AADJ observed but device-only.
        /// Independent of <see cref="EnrollmentJoinMode"/>, which reflects the
        /// <c>SessionStarted</c> registry hint.
        /// </summary>
        public SignalFact<bool>? AadUserJoinWithUserObserved { get; }

        /// <summary>Raw payload half-fact from <see cref="Signals.DecisionSignalKind.EspConfigDetected"/>.</summary>
        public SignalFact<bool>? SkipUserEsp { get; }

        /// <summary>Raw payload half-fact from <see cref="Signals.DecisionSignalKind.EspConfigDetected"/>.</summary>
        public SignalFact<bool>? SkipDeviceEsp { get; }

        public EnrollmentScenarioObservations WithShellCoreWhiteGloveSuccessSeen(long sourceSignalOrdinal) =>
            ShellCoreWhiteGloveSuccessSeen != null
                ? this
                : new EnrollmentScenarioObservations(
                    new SignalFact<bool>(true, sourceSignalOrdinal),
                    WhiteGloveSealingPatternSeen,
                    AadUserJoinWithUserObserved,
                    SkipUserEsp,
                    SkipDeviceEsp);

        public EnrollmentScenarioObservations WithWhiteGloveSealingPatternSeen(long sourceSignalOrdinal) =>
            WhiteGloveSealingPatternSeen != null
                ? this
                : new EnrollmentScenarioObservations(
                    ShellCoreWhiteGloveSuccessSeen,
                    new SignalFact<bool>(true, sourceSignalOrdinal),
                    AadUserJoinWithUserObserved,
                    SkipUserEsp,
                    SkipDeviceEsp);

        public EnrollmentScenarioObservations WithAadUserJoinWithUserObserved(bool value, long sourceSignalOrdinal) =>
            AadUserJoinWithUserObserved != null
                ? this
                : new EnrollmentScenarioObservations(
                    ShellCoreWhiteGloveSuccessSeen,
                    WhiteGloveSealingPatternSeen,
                    new SignalFact<bool>(value, sourceSignalOrdinal),
                    SkipUserEsp,
                    SkipDeviceEsp);

        public EnrollmentScenarioObservations WithSkipUserEsp(bool value, long sourceSignalOrdinal) =>
            SkipUserEsp != null
                ? this
                : new EnrollmentScenarioObservations(
                    ShellCoreWhiteGloveSuccessSeen,
                    WhiteGloveSealingPatternSeen,
                    AadUserJoinWithUserObserved,
                    new SignalFact<bool>(value, sourceSignalOrdinal),
                    SkipDeviceEsp);

        public EnrollmentScenarioObservations WithSkipDeviceEsp(bool value, long sourceSignalOrdinal) =>
            SkipDeviceEsp != null
                ? this
                : new EnrollmentScenarioObservations(
                    ShellCoreWhiteGloveSuccessSeen,
                    WhiteGloveSealingPatternSeen,
                    AadUserJoinWithUserObserved,
                    SkipUserEsp,
                    new SignalFact<bool>(value, sourceSignalOrdinal));
    }
}
