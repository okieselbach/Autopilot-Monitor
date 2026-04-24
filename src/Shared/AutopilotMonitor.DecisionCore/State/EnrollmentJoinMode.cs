namespace AutopilotMonitor.DecisionCore.State
{
    /// <summary>
    /// Directory-join method observed at session start. Codex follow-up #5 —
    /// dimension of <see cref="EnrollmentScenarioProfile"/>. Derived from the
    /// <c>isHybridJoin</c> payload on the <c>SessionStarted</c> signal
    /// (<c>EnrollmentRegistryDetector.DetectHybridJoin</c>).
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
