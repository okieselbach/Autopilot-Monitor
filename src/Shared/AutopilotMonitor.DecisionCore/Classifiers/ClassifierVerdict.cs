using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.DecisionCore.Classifiers
{
    /// <summary>
    /// Immutable verdict record produced by an <see cref="IClassifier"/>. Plan §2.4.
    /// <para>
    /// Emitted back into the signal log as a synthetic
    /// <c>ClassifierVerdictIssued</c> signal (L.8) and attached to the corresponding
    /// <c>DecisionTransition.ClassifierVerdictJson</c>.
    /// </para>
    /// </summary>
    public sealed class ClassifierVerdict
    {
        public ClassifierVerdict(
            string classifierId,
            HypothesisLevel level,
            int score,
            IReadOnlyList<string> contributingFactors,
            string reason,
            string inputHash)
        {
            if (string.IsNullOrEmpty(classifierId))
            {
                throw new ArgumentException("ClassifierId is mandatory.", nameof(classifierId));
            }

            if (string.IsNullOrEmpty(inputHash))
            {
                throw new ArgumentException("InputHash is mandatory.", nameof(inputHash));
            }

            if (score < 0 || score > 100)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(score),
                    "Score must be in [0, 100].");
            }

            ClassifierId = classifierId;
            Level = level;
            Score = score;
            ContributingFactors = contributingFactors ?? Array.Empty<string>();
            Reason = reason ?? string.Empty;
            InputHash = inputHash;
        }

        public string ClassifierId { get; }

        public HypothesisLevel Level { get; }

        public int Score { get; }

        public IReadOnlyList<string> ContributingFactors { get; }

        public string Reason { get; }

        /// <summary>SHA256-based hex hash (16 chars) over classifier-specific snapshot. Plan §2.4 — anti-loop.</summary>
        public string InputHash { get; }
    }
}
