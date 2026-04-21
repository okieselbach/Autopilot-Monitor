using System;
using System.Security.Cryptography;
using System.Text;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.DecisionCore.Classifiers
{
    /// <summary>
    /// Input snapshot for <see cref="WhiteGlovePart2CompletionClassifier"/>.
    /// Plan §2.4 / §M3.4 — the four Part-2 post-reboot user signals drive the score.
    /// </summary>
    public sealed class WhiteGlovePart2CompletionSnapshot : IClassifierSnapshot
    {
        public WhiteGlovePart2CompletionSnapshot(
            bool userAadSignInComplete,
            bool helloResolvedPart2,
            bool desktopArrivedPart2,
            bool accountSetupCompletedPart2,
            EnrollmentPhase? currentEnrollmentPhase,
            DateTime? systemRebootUtc)
        {
            UserAadSignInComplete = userAadSignInComplete;
            HelloResolvedPart2 = helloResolvedPart2;
            DesktopArrivedPart2 = desktopArrivedPart2;
            AccountSetupCompletedPart2 = accountSetupCompletedPart2;
            CurrentEnrollmentPhase = currentEnrollmentPhase;
            SystemRebootUtc = systemRebootUtc;
        }

        public bool UserAadSignInComplete { get; }
        public bool HelloResolvedPart2 { get; }
        public bool DesktopArrivedPart2 { get; }
        public bool AccountSetupCompletedPart2 { get; }
        public EnrollmentPhase? CurrentEnrollmentPhase { get; }
        public DateTime? SystemRebootUtc { get; }

        public string ComputeInputHash()
        {
            var canonical = string.Join(
                "|",
                "whiteglove-part2-completion-v1",
                UserAadSignInComplete ? "1" : "0",
                HelloResolvedPart2 ? "1" : "0",
                DesktopArrivedPart2 ? "1" : "0",
                AccountSetupCompletedPart2 ? "1" : "0",
                CurrentEnrollmentPhase?.ToString() ?? "null",
                SystemRebootUtc?.ToString("O") ?? "null");

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
