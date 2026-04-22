using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
    /// V1 parity: on mismatch an <see cref="EmergencyReporter"/> / <c>AgentErrorType.IntegrityCheckFailed</c>
    /// report is emitted by the caller, and — if <paramref name="runtimeSelfUpdateTrigger"/> is wired —
    /// <see cref="SelfUpdater.CheckAndApplyUpdateAsync"/> is invoked in force-update mode to auto-heal.
    /// The trigger is single-shot per agent process (<see cref="Interlocked"/> guard) so a noisy
    /// config fetch cannot drive the update pipeline multiple times in a row.
    /// </para>
    /// </summary>
    public static class BinaryIntegrityVerifier
    {
        private static int _runtimeSelfUpdateFired;

        /// <summary>
        /// Computes the SHA-256 of <paramref name="exePath"/> (or the current process' main-module
        /// image when <c>null</c>) and compares it against <paramref name="expectedSha256"/>.
        /// Case-insensitive comparison (both lowercase and uppercase hex strings accepted).
        /// <para>
        /// When <paramref name="runtimeSelfUpdateTrigger"/> is non-null and the hashes mismatch,
        /// fires the trigger once per agent process — typically wired to
        /// <c>SelfUpdater.CheckAndApplyUpdateAsync(forceUpdate: true, triggerReason: "runtime_hash_mismatch")</c>.
        /// </para>
        /// </summary>
        /// <param name="expectedSha256">Backend-advertised SHA-256 (lowercase hex preferred).</param>
        /// <param name="logger">Agent logger. Match uses Info, mismatch uses Error (V1 parity).</param>
        /// <param name="exePath">Optional explicit path. <c>null</c> = use <c>Process.GetCurrentProcess().MainModule.FileName</c>.</param>
        /// <param name="runtimeSelfUpdateTrigger">Optional auto-heal hook. Params: <c>(zipHash, allowDowngrade) → Task</c>.</param>
        /// <param name="zipHash">Value of <c>AgentConfigResponse.LatestAgentSha256</c> — forwarded to the trigger.</param>
        /// <param name="allowDowngrade">Value of <c>AgentConfigResponse.AllowAgentDowngrade</c> — forwarded to the trigger.</param>
        public static IntegrityCheckResult Check(
            string expectedSha256,
            AgentLogger logger,
            string exePath = null,
            Func<string, bool, Task> runtimeSelfUpdateTrigger = null,
            string zipHash = null,
            bool allowDowngrade = false)
        {
            if (logger == null) throw new ArgumentNullException(nameof(logger));

            if (string.IsNullOrWhiteSpace(expectedSha256))
            {
                logger.Debug("Post-config integrity check: no expected hash provided — skipping.");
                return IntegrityCheckResult.Skipped();
            }

            var path = exePath ?? ResolveMainModulePath();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                logger.Warning($"Post-config integrity check: cannot resolve EXE path ({path ?? "null"}) — skipping.");
                return IntegrityCheckResult.Skipped();
            }

            string actualSha;
            try
            {
                actualSha = ComputeSha256Hex(path);
            }
            catch (Exception ex)
            {
                logger.Warning($"Post-config integrity check: hashing failed — {ex.Message}. Skipping.");
                return IntegrityCheckResult.Skipped();
            }

            if (string.Equals(actualSha, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                logger.Info($"Post-config integrity check: SHA-256 verified OK ({Truncate(actualSha)}).");
                return IntegrityCheckResult.Match(actualSha);
            }

            logger.Error(
                $"Post-config integrity check: SHA-256 MISMATCH — running exe={Truncate(actualSha)}, " +
                $"backend advertises={Truncate(expectedSha256)} — possible tamper, stale blob, or failed self-update.");

            TriggerRuntimeSelfUpdate(logger, runtimeSelfUpdateTrigger, zipHash, allowDowngrade);

            return IntegrityCheckResult.Mismatch(actualSha, expectedSha256);
        }

        /// <summary>Exposed for tests — resets the single-shot trigger so each test starts clean.</summary>
        internal static void ResetTriggerForTests() => Interlocked.Exchange(ref _runtimeSelfUpdateFired, 0);

        private static void TriggerRuntimeSelfUpdate(
            AgentLogger logger,
            Func<string, bool, Task> runtimeSelfUpdateTrigger,
            string zipHash,
            bool allowDowngrade)
        {
            if (runtimeSelfUpdateTrigger == null)
            {
                logger.Warning("Runtime self-update trigger not wired — mismatch reported but no auto-heal possible.");
                return;
            }

            if (Interlocked.Exchange(ref _runtimeSelfUpdateFired, 1) == 1)
            {
                logger.Debug("Runtime self-update trigger already fired this session — ignoring.");
                return;
            }

            // Run the auto-heal on the thread pool — SelfUpdater.CheckAndApplyUpdateAsync
            // blocks for the version/ZIP fetch and, on success, calls Environment.Exit before
            // returning, so the awaiter never resolves. Do NOT block the caller on this.
            Task.Run(async () =>
            {
                try
                {
                    logger.Info($"Runtime self-update trigger: invoking SelfUpdater (forceUpdate=true, allowDowngrade={allowDowngrade}).");
                    await runtimeSelfUpdateTrigger(zipHash, allowDowngrade).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.Error("Runtime self-update trigger failed.", ex);
                }
            });
        }

        private static string ResolveMainModulePath()
        {
            try
            {
                // V1 parity — MonitoringService.VerifyAgentBinaryIntegrity uses
                // Process.GetCurrentProcess().MainModule.FileName, NOT Assembly.Location.
                // Under ngen / single-file publish scenarios MainModule resolves to the real
                // on-disk image while Assembly.Location can be empty or point at a shadow copy.
                return Process.GetCurrentProcess().MainModule?.FileName;
            }
            catch
            {
                return null;
            }
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
