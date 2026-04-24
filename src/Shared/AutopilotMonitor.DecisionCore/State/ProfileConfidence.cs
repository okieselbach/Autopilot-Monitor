namespace AutopilotMonitor.DecisionCore.State
{
    /// <summary>
    /// Overall confidence in the current <see cref="EnrollmentScenarioProfile"/> classification.
    /// Codex follow-up #5. Monotonic by convention: the updater never regresses confidence —
    /// a later signal with weaker evidence cannot overwrite a stronger classification.
    /// <list type="bullet">
    ///   <item><see cref="Low"/> — initial / registry-derived hint (e.g. <c>SessionStarted</c> only).</item>
    ///   <item><see cref="Medium"/> — corroborating signal observed (e.g. ESP phase change, profile read).</item>
    ///   <item><see cref="High"/> — decisive signal (e.g. IME pattern match, classifier Confirmed).</item>
    /// </list>
    /// </summary>
    public enum ProfileConfidence
    {
        Low = 0,
        Medium = 1,
        High = 2,
    }
}
