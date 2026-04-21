namespace AutopilotMonitor.DecisionCore.State
{
    /// <summary>
    /// Confidence levels for an engine hypothesis. Plan §2.3.
    /// </summary>
    public enum HypothesisLevel
    {
        Unknown = 0,
        Weak = 1,
        Strong = 2,
        Confirmed = 3,
        Rejected = 4,

        /// <summary>
        /// Plan §2.7b — Classifier exception / best-effort failure.
        /// Hypothesis keeps its prior level; verdict recorded as Inconclusive, no abort.
        /// </summary>
        Inconclusive = 5,
    }
}
