namespace AutopilotMonitor.DecisionCore.State
{
    /// <summary>
    /// Directory-join method observed at session start. Codex follow-up #5 —
    /// dimension of <see cref="EnrollmentScenarioProfile"/>. Derived from the
    /// <c>isHybridJoin</c> payload on the
    /// <see cref="Signals.DecisionSignalKind.EnrollmentFactsObserved"/> signal
    /// (<c>EnrollmentRegistryDetector.DetectHybridJoin</c>). V2 race-fix
    /// (10c8e0bf debrief, 2026-04-26): previously sourced from <c>SessionStarted</c>,
    /// which had a Stage-Wache that swallowed the update on race conditions.
    /// <para>
    /// This is NOT the same concept as the legacy <c>AadJoinedWithUser</c> fact, which
    /// tracked a late-AADJ user-presence signal for the WhiteGlove hard-excluder weight.
    /// That observation lives in <see cref="EnrollmentScenarioObservations.AadUserJoinWithUserObserved"/>.
    /// </para>
    /// </summary>
    public enum EnrollmentJoinMode
    {
        Unknown = 0,
        AzureAdJoin = 1,
        HybridAzureAdJoin = 2,
    }
}
