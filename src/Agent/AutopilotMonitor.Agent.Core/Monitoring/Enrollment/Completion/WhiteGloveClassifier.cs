using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Completion
{
    /// <summary>
    /// Confidence level for a WhiteGlove Part 1 classification.
    /// </summary>
    internal enum WhiteGloveConfidence
    {
        /// <summary>Score below <see cref="WhiteGloveClassifier.LowThreshold"/> — definitely not WhiteGlove.</summary>
        None,

        /// <summary>Score between low and high thresholds — ambiguous. Routed as NOT WhiteGlove (asymmetric-conservative).</summary>
        Weak,

        /// <summary>Score at or above <see cref="WhiteGloveClassifier.HighThreshold"/> — classify as WhiteGlove Part 1.</summary>
        Strong,
    }

    /// <summary>
    /// Result of a <see cref="WhiteGloveClassifier.Classify"/> call. Contains the
    /// final routing decision, the numeric score and the contributing factors for
    /// observability / tuning.
    /// </summary>
    internal sealed class WhiteGloveClassification
    {
        public WhiteGloveConfidence Confidence { get; set; }
        public int Score { get; set; }
        public List<string> ContributingFactors { get; set; } = new List<string>();
        public string Reason { get; set; }

        /// <summary>
        /// Single Boolean the caller should read to decide whether to route to
        /// <c>whiteglove_complete</c> (Pending) instead of the normal completion path.
        /// Only true when <see cref="Confidence"/> is <see cref="WhiteGloveConfidence.Strong"/>.
        /// </summary>
        public bool ShouldRouteToWhiteGlovePart1 { get; set; }
    }

    /// <summary>
    /// Scoring-based WhiteGlove Part 1 classifier — modelled on the backend
    /// <c>AnalyzeRule.BaseConfidence + ConfidenceFactors + ConfidenceThreshold</c> pattern.
    ///
    /// Asymmetric-conservative decision rule: only a <see cref="WhiteGloveConfidence.Strong"/>
    /// score routes to WhiteGlove. A <see cref="WhiteGloveConfidence.Weak"/> score is emitted
    /// to telemetry for tuning but treated as NOT WhiteGlove. Rationale: a false-positive WG
    /// classification keeps the agent pending when it should exit; a false-negative emits
    /// <c>enrollment_complete</c> early and the later user sign-in triggers a new session —
    /// the user-visible cost of false-negative is much lower than false-positive.
    /// </summary>
    internal static class WhiteGloveClassifier
    {
        // Thresholds (tunable after production data — see project memory).
        internal const int HighThreshold = 70; // >= 70 → Strong → route to WG
        internal const int LowThreshold  = 30; // 30..69 → Weak (telemetry only)
        internal const int BaseConfidence = 0;

        // Positive-signal weights
        internal const int WeightShellCoreWhiteGloveSuccess   = 80; // definitive — Event 62407
        internal const int WeightHasSaveWhiteGloveSuccess     = 10; // very weak — also fires on non-WG devices
        internal const int WeightWhiteGloveStartDetected      = 15; // soft — Event 509 also fires on hybrid
        internal const int WeightFooUserDetected              = 20; // weak — pre-provisioning marker
        internal const int WeightAgentRestartedAfterEspExit   = 10; // weak — typical WG boundary
        internal const int WeightDeviceOnlyDeployment         = 15; // context boost

        // Negative-signal weights (hard excluders)
        internal const int WeightAadJoinedWithUser   = -100; // hard exclude
        internal const int WeightDesktopArrived      = -100; // hard exclude
        internal const int WeightAccountSetupActive  =  -40; // soft exclude

        public static WhiteGloveClassification Classify(WhiteGloveSignals s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));

            var factors = new List<string>();
            int score = BaseConfidence;

            // Hard excluders
            if (s.AadJoinedWithUser)
            {
                score += WeightAadJoinedWithUser;
                factors.Add($"aad_joined_with_user:{WeightAadJoinedWithUser:+#;-#;0}");
            }
            if (s.DesktopArrived)
            {
                score += WeightDesktopArrived;
                factors.Add($"desktop_arrived:{WeightDesktopArrived:+#;-#;0}");
            }
            if (s.HasAccountSetupActivity)
            {
                score += WeightAccountSetupActive;
                factors.Add($"account_setup_activity:{WeightAccountSetupActive:+#;-#;0}");
            }

            // Positive signals — weighted by empirical reliability
            if (s.ShellCoreWhiteGloveSuccess)
            {
                score += WeightShellCoreWhiteGloveSuccess;
                factors.Add($"shellcore_wg_success:{WeightShellCoreWhiteGloveSuccess:+#;-#;0}");
            }
            if (s.HasSaveWhiteGloveSuccessResult)
            {
                score += WeightHasSaveWhiteGloveSuccess;
                factors.Add($"save_wg_success:{WeightHasSaveWhiteGloveSuccess:+#;-#;0}");
            }
            if (s.IsWhiteGloveStartDetected)
            {
                score += WeightWhiteGloveStartDetected;
                factors.Add($"event509_wg_start:{WeightWhiteGloveStartDetected:+#;-#;0}");
            }
            if (s.IsFooUserDetected)
            {
                score += WeightFooUserDetected;
                factors.Add($"foouser_detected:{WeightFooUserDetected:+#;-#;0}");
            }
            if (s.AgentRestartedAfterEspExit)
            {
                score += WeightAgentRestartedAfterEspExit;
                factors.Add($"agent_restart_after_esp:{WeightAgentRestartedAfterEspExit:+#;-#;0}");
            }
            if (s.IsDeviceOnlyDeployment)
            {
                score += WeightDeviceOnlyDeployment;
                factors.Add($"device_only_deployment:{WeightDeviceOnlyDeployment:+#;-#;0}");
            }

            // NOTE: no hybrid-join dampener — WhiteGlove works on both AADJ and hybrid.
            // NOTE: no "long stable since DeviceSetup" bonus — devices stand idle for other reasons.

            // Cap to [0, 100]
            if (score < 0) score = 0;
            if (score > 100) score = 100;

            WhiteGloveConfidence confidence;
            if (score >= HighThreshold)      confidence = WhiteGloveConfidence.Strong;
            else if (score >= LowThreshold)  confidence = WhiteGloveConfidence.Weak;
            else                             confidence = WhiteGloveConfidence.None;

            return new WhiteGloveClassification
            {
                Confidence = confidence,
                Score = score,
                ContributingFactors = factors,
                Reason = $"confidence={confidence} score={score}",
                ShouldRouteToWhiteGlovePart1 = (confidence == WhiteGloveConfidence.Strong),
            };
        }
    }
}
