namespace AutopilotMonitor.DecisionCore.State
{
    /// <summary>
    /// Typed aggregate of the derived enrollment-scenario classification. Codex follow-up #5 —
    /// replaces the legacy <c>Hypothesis EnrollmentType</c> and the <c>SkipUserEsp</c> /
    /// <c>SkipDeviceEsp</c> facts, and adds an explicit <see cref="JoinMode"/> +
    /// <see cref="PreProvisioningSide"/> dimension. This is the **semantic truth** of the
    /// enrollment — raw per-signal evidence lives in <see cref="EnrollmentScenarioObservations"/>
    /// and classifier verdicts live in <see cref="ClassifierOutcomes"/>.
    /// <para>
    /// <b>Invariants</b>:
    /// <list type="bullet">
    ///   <item>Immutable; use <see cref="With"/> to produce a mutated copy.</item>
    ///   <item>Confidence monotonicity is the caller's responsibility (see <see cref="EnrollmentScenarioProfileUpdater"/>) — the type itself does not enforce it, keeping deserialization after recovery straightforward.</item>
    ///   <item><see cref="EvidenceOrdinal"/> is the <see cref="Signals.DecisionSignal.SessionSignalOrdinal"/> of the last signal that strengthened the profile; <c>-1</c> when <see cref="Empty"/>.</item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class EnrollmentScenarioProfile
    {
        public static readonly EnrollmentScenarioProfile Empty = new EnrollmentScenarioProfile(
            mode: EnrollmentMode.Unknown,
            joinMode: EnrollmentJoinMode.Unknown,
            espConfig: EspConfig.Unknown,
            preProvisioningSide: PreProvisioningSide.None,
            confidence: ProfileConfidence.Low,
            evidenceOrdinal: -1,
            reason: null);

        public EnrollmentScenarioProfile(
            EnrollmentMode mode,
            EnrollmentJoinMode joinMode,
            EspConfig espConfig,
            PreProvisioningSide preProvisioningSide,
            ProfileConfidence confidence,
            long evidenceOrdinal,
            string? reason)
        {
            Mode = mode;
            JoinMode = joinMode;
            EspConfig = espConfig;
            PreProvisioningSide = preProvisioningSide;
            Confidence = confidence;
            EvidenceOrdinal = evidenceOrdinal;
            Reason = reason;
        }

        public EnrollmentMode Mode { get; }

        public EnrollmentJoinMode JoinMode { get; }

        public EspConfig EspConfig { get; }

        public PreProvisioningSide PreProvisioningSide { get; }

        public ProfileConfidence Confidence { get; }

        /// <summary>Signal ordinal that most recently strengthened the profile, or <c>-1</c> if never.</summary>
        public long EvidenceOrdinal { get; }

        /// <summary>Short token describing the last strengthening reason (analog to <see cref="Hypothesis.Reason"/>).</summary>
        public string? Reason { get; }

        /// <summary>
        /// Produce a copy with the specified dimensions replaced. Omitting a parameter keeps the
        /// current value — including <see cref="Reason"/>, which means callers cannot clear the
        /// reason to <c>null</c> via <see cref="With"/> (intentional: once set, reasons are
        /// historical evidence).
        /// </summary>
        public EnrollmentScenarioProfile With(
            EnrollmentMode? mode = null,
            EnrollmentJoinMode? joinMode = null,
            EspConfig? espConfig = null,
            PreProvisioningSide? preProvisioningSide = null,
            ProfileConfidence? confidence = null,
            long? evidenceOrdinal = null,
            string? reason = null) =>
            new EnrollmentScenarioProfile(
                mode ?? Mode,
                joinMode ?? JoinMode,
                espConfig ?? EspConfig,
                preProvisioningSide ?? PreProvisioningSide,
                confidence ?? Confidence,
                evidenceOrdinal ?? EvidenceOrdinal,
                reason ?? Reason);
    }
}
