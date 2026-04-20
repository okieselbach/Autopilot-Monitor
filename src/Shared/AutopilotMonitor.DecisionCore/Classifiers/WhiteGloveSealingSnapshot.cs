using System;
using System.Security.Cryptography;
using System.Text;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.DecisionCore.Classifiers
{
    /// <summary>
    /// Input snapshot for <see cref="WhiteGloveSealingClassifier"/>. Plan §2.4 input-hash.
    /// <para>
    /// Mirrors the WG-relevant fields from <c>DecisionState</c> that participate in scoring.
    /// Extra fields (unrelated facts, deadlines, etc.) intentionally excluded — including them
    /// would cause unnecessary re-runs on the anti-loop guard.
    /// </para>
    /// </summary>
    public sealed class WhiteGloveSealingSnapshot
    {
        public WhiteGloveSealingSnapshot(
            bool shellCoreWhiteGloveSuccessSeen,
            bool whiteGloveSealingPatternSeen,
            bool aadJoinedWithUser,
            bool desktopArrived,
            bool helloResolved,
            bool hasAccountSetupActivity,
            bool isDeviceOnlyDeploymentHypothesis,
            DateTime? systemRebootUtc,
            EnrollmentPhase? currentEnrollmentPhase)
        {
            ShellCoreWhiteGloveSuccessSeen = shellCoreWhiteGloveSuccessSeen;
            WhiteGloveSealingPatternSeen = whiteGloveSealingPatternSeen;
            AadJoinedWithUser = aadJoinedWithUser;
            DesktopArrived = desktopArrived;
            HelloResolved = helloResolved;
            HasAccountSetupActivity = hasAccountSetupActivity;
            IsDeviceOnlyDeploymentHypothesis = isDeviceOnlyDeploymentHypothesis;
            SystemRebootUtc = systemRebootUtc;
            CurrentEnrollmentPhase = currentEnrollmentPhase;
        }

        public bool ShellCoreWhiteGloveSuccessSeen { get; }
        public bool WhiteGloveSealingPatternSeen { get; }
        public bool AadJoinedWithUser { get; }
        public bool DesktopArrived { get; }
        public bool HelloResolved { get; }
        public bool HasAccountSetupActivity { get; }
        public bool IsDeviceOnlyDeploymentHypothesis { get; }
        public DateTime? SystemRebootUtc { get; }
        public EnrollmentPhase? CurrentEnrollmentPhase { get; }

        /// <summary>
        /// Deterministic 16-hex-char SHA256 prefix over the snapshot. Anti-loop guard:
        /// the classifier is skipped if this hash equals the last recorded
        /// <see cref="State.Hypothesis.LastClassifierVerdictId"/>. Plan §2.4.
        /// </summary>
        public string ComputeInputHash()
        {
            var canonical = string.Join(
                "|",
                "whiteglove-sealing-v1",
                ShellCoreWhiteGloveSuccessSeen ? "1" : "0",
                WhiteGloveSealingPatternSeen ? "1" : "0",
                AadJoinedWithUser ? "1" : "0",
                DesktopArrived ? "1" : "0",
                HelloResolved ? "1" : "0",
                HasAccountSetupActivity ? "1" : "0",
                IsDeviceOnlyDeploymentHypothesis ? "1" : "0",
                SystemRebootUtc?.ToString("O") ?? "null",
                CurrentEnrollmentPhase?.ToString() ?? "null");

            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
                var sb = new StringBuilder(16);
                for (int i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
