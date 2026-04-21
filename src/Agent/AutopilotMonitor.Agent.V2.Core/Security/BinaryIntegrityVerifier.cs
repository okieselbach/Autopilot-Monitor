using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AutopilotMonitor.Agent.V2.Core.Logging;

namespace AutopilotMonitor.Agent.V2.Core.Security
{
    /// <summary>
    /// Post-config binary-integrity check. After the remote config is fetched, the agent compares
    /// the SHA-256 of its running <c>.exe</c> against <c>AgentConfigResponse.LatestAgentExeSha256</c>.
    /// A mismatch means one of:
    /// <list type="bullet">
    ///   <item>tampering — the on-disk binary was modified after deployment,</item>
    ///   <item>stale blob storage — the agent is running an older EXE than what the backend advertises,</item>
    ///   <item>self-update failed silently — a previous update swapped the ZIP but not the EXE.</item>
    /// </list>
    /// <para>
    /// We intentionally do <b>not</b> terminate or auto-update from this check: <see cref="SelfUpdater"/>
    /// already runs at startup and its hash-based trigger covers the "wrong EXE" case. All we do
    /// here is emit an <c>AgentErrorType.IntegrityCheckFailed</c> report so operators see the drift.
    /// </para>
    /// </summary>
    public static class BinaryIntegrityVerifier
    {
        /// <summary>
        /// Computes the SHA-256 of <paramref name="exePath"/> (or the currently executing assembly
        /// when <c>null</c>) and compares it against <paramref name="expectedSha256"/>.
        /// Case-insensitive comparison (both lowercase and uppercase hex strings accepted).
        /// </summary>
        public static IntegrityCheckResult Check(
            string expectedSha256,
            AgentLogger logger,
            string exePath = null)
        {
            if (logger == null) throw new ArgumentNullException(nameof(logger));

            if (string.IsNullOrWhiteSpace(expectedSha256))
            {
                logger.Debug("BinaryIntegrityVerifier: no expected hash provided — skipping check.");
                return IntegrityCheckResult.Skipped();
            }

            var path = exePath ?? Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                logger.Warning($"BinaryIntegrityVerifier: executing assembly path not resolvable ({path ?? "null"}) — skipping check.");
                return IntegrityCheckResult.Skipped();
            }

            string actualSha;
            try
            {
                actualSha = ComputeSha256Hex(path);
            }
            catch (Exception ex)
            {
                logger.Warning($"BinaryIntegrityVerifier: failed to hash '{path}': {ex.Message}");
                return IntegrityCheckResult.Skipped();
            }

            if (string.Equals(actualSha, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                logger.Debug($"BinaryIntegrityVerifier: SHA-256 match (sha={Truncate(actualSha)}).");
                return IntegrityCheckResult.Match(actualSha);
            }

            logger.Warning(
                $"BinaryIntegrityVerifier: SHA-256 MISMATCH — running exe={Truncate(actualSha)}, backend advertises={Truncate(expectedSha256)}. " +
                "Possible tamper, stale blob, or failed self-update.");
            return IntegrityCheckResult.Mismatch(actualSha, expectedSha256);
        }

        internal static string ComputeSha256Hex(string filePath)
        {
            using (var stream = File.OpenRead(filePath))
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(stream);
                var sb = new StringBuilder(bytes.Length * 2);
                for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }

        private static string Truncate(string hex)
            => string.IsNullOrEmpty(hex) ? "(null)" : (hex.Length <= 12 ? hex : hex.Substring(0, 12) + "...");
    }

    public sealed class IntegrityCheckResult
    {
        public IntegrityCheckOutcome Outcome { get; }
        public string ActualSha256 { get; }
        public string ExpectedSha256 { get; }

        private IntegrityCheckResult(IntegrityCheckOutcome outcome, string actual, string expected)
        {
            Outcome = outcome;
            ActualSha256 = actual;
            ExpectedSha256 = expected;
        }

        public static IntegrityCheckResult Skipped() => new IntegrityCheckResult(IntegrityCheckOutcome.Skipped, null, null);
        public static IntegrityCheckResult Match(string actual) => new IntegrityCheckResult(IntegrityCheckOutcome.Match, actual, actual);
        public static IntegrityCheckResult Mismatch(string actual, string expected) => new IntegrityCheckResult(IntegrityCheckOutcome.Mismatch, actual, expected);

        public bool IsMismatch => Outcome == IntegrityCheckOutcome.Mismatch;
    }

    public enum IntegrityCheckOutcome
    {
        /// <summary>No expected hash provided, or the local path could not be hashed.</summary>
        Skipped = 0,

        /// <summary>SHA-256 of the running EXE matches the backend-advertised hash.</summary>
        Match = 1,

        /// <summary>SHA-256 differs — tamper / stale blob / failed self-update.</summary>
        Mismatch = 2,
    }
}
