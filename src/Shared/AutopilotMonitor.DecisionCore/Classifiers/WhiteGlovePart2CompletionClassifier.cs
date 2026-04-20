using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.DecisionCore.Classifiers
{
    /// <summary>
    /// Scoring classifier for WhiteGlove Part 2 completion. Plan §2.4 / §M3.4.
    /// <para>
    /// Scoring rules (new — no Legacy equivalent): each of the four Part-2 post-reboot user
    /// signals contributes +25; all four together cross the Confirmed threshold of 100.
    /// Missing one signal gives 75 -> Weak. This matches the plan's requirement that Part-2
    /// completion needs the full UserSignIn + Hello + Desktop + AccountSetup set.
    /// </para>
    /// </summary>
    public sealed class WhiteGlovePart2CompletionClassifier : IClassifier
    {
        public const string ClassifierId = "whiteglove-part2-completion";

        internal const int HighThreshold = 100; // exactly-all-four -> Confirmed
        internal const int LowThreshold = 25;   // any single signal -> Weak

        internal const int WeightUserAadSignIn = 25;
        internal const int WeightHelloResolved = 25;
        internal const int WeightDesktopArrived = 25;
        internal const int WeightAccountSetup = 25;

        public string Id => ClassifierId;
        public Type SnapshotType => typeof(WhiteGlovePart2CompletionSnapshot);

        public ClassifierVerdict Classify(object snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (!(snapshot is WhiteGlovePart2CompletionSnapshot s))
            {
                throw new ArgumentException(
                    $"Expected {nameof(WhiteGlovePart2CompletionSnapshot)}, got {snapshot.GetType().Name}.",
                    nameof(snapshot));
            }

            var factors = new List<string>();
            int score = 0;

            if (s.UserAadSignInComplete)   { score += WeightUserAadSignIn;   factors.Add($"user_aad_signin:{WeightUserAadSignIn:+#;-#;0}"); }
            if (s.HelloResolvedPart2)      { score += WeightHelloResolved;   factors.Add($"hello_part2:{WeightHelloResolved:+#;-#;0}"); }
            if (s.DesktopArrivedPart2)     { score += WeightDesktopArrived;  factors.Add($"desktop_part2:{WeightDesktopArrived:+#;-#;0}"); }
            if (s.AccountSetupCompletedPart2) { score += WeightAccountSetup; factors.Add($"account_setup_part2:{WeightAccountSetup:+#;-#;0}"); }

            if (score < 0) score = 0;
            if (score > 100) score = 100;

            HypothesisLevel level;
            if (score >= HighThreshold)     level = HypothesisLevel.Confirmed;
            else if (score >= LowThreshold) level = HypothesisLevel.Weak;
            else                            level = HypothesisLevel.Unknown;

            return new ClassifierVerdict(
                classifierId: ClassifierId,
                level: level,
                score: score,
                contributingFactors: factors,
                reason: $"confidence={level} score={score}",
                inputHash: s.ComputeInputHash());
        }
    }
}
