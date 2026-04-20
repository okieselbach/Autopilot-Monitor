using System;

namespace AutopilotMonitor.DecisionCore.State
{
    /// <summary>
    /// Immutable engine hypothesis record. Plan §2.3.
    /// Replaces boolean flag state in trackers with an explicit
    /// level/score/reason/evidence-trace tuple.
    /// </summary>
    public sealed class Hypothesis
    {
        public static readonly Hypothesis UnknownInstance = new Hypothesis(
            HypothesisLevel.Unknown,
            reason: null,
            score: 0,
            lastUpdatedUtc: DateTime.MinValue,
            lastClassifierVerdictId: null);

        public Hypothesis(
            HypothesisLevel level,
            string? reason,
            int score,
            DateTime lastUpdatedUtc,
            string? lastClassifierVerdictId)
        {
            if (score < 0 || score > 100)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(score),
                    "Score must be in [0, 100].");
            }

            Level = level;
            Reason = reason;
            Score = score;
            LastUpdatedUtc = lastUpdatedUtc;
            LastClassifierVerdictId = lastClassifierVerdictId;
        }

        public HypothesisLevel Level { get; }

        public string? Reason { get; }

        /// <summary>0..100 — classifier score or reducer-assigned confidence.</summary>
        public int Score { get; }

        public DateTime LastUpdatedUtc { get; }

        /// <summary>Journal reference to the classifier verdict that last touched this hypothesis, if any.</summary>
        public string? LastClassifierVerdictId { get; }

        public Hypothesis With(
            HypothesisLevel? level = null,
            string? reason = null,
            int? score = null,
            DateTime? lastUpdatedUtc = null,
            string? lastClassifierVerdictId = null) =>
            new Hypothesis(
                level ?? Level,
                reason ?? Reason,
                score ?? Score,
                lastUpdatedUtc ?? LastUpdatedUtc,
                lastClassifierVerdictId ?? LastClassifierVerdictId);
    }
}
