using System.IO;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Security
{
    public sealed class BinaryIntegrityVerifierTests
    {
        private static AgentLogger NewLogger(TempDirectory tmp)
            => new AgentLogger(Path.Combine(tmp.Path, "logs"), AgentLogLevel.Debug);

        [Fact]
        public void Check_returns_skipped_when_expected_hash_is_null_or_empty()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp);

            Assert.Equal(IntegrityCheckOutcome.Skipped, BinaryIntegrityVerifier.Check(null!, logger).Outcome);
            Assert.Equal(IntegrityCheckOutcome.Skipped, BinaryIntegrityVerifier.Check("", logger).Outcome);
            Assert.Equal(IntegrityCheckOutcome.Skipped, BinaryIntegrityVerifier.Check("   ", logger).Outcome);
        }

        [Fact]
        public void Check_returns_skipped_when_target_path_does_not_exist()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp);
            var missing = Path.Combine(tmp.Path, "no-such.exe");

            var result = BinaryIntegrityVerifier.Check("0".PadLeft(64, '0'), logger, exePath: missing);

            Assert.Equal(IntegrityCheckOutcome.Skipped, result.Outcome);
        }

        [Fact]
        public void Check_returns_match_when_hash_equals_actual()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp);

            var exe = Path.Combine(tmp.Path, "fake.exe");
            File.WriteAllBytes(exe, new byte[] { 0x01, 0x02, 0x03, 0x04 });
            var actual = BinaryIntegrityVerifier.ComputeSha256Hex(exe);

            var result = BinaryIntegrityVerifier.Check(actual, logger, exePath: exe);

            Assert.Equal(IntegrityCheckOutcome.Match, result.Outcome);
            Assert.False(result.IsMismatch);
            Assert.Equal(actual, result.ActualSha256);
        }

        [Fact]
        public void Check_is_case_insensitive_for_expected_hash()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp);

            var exe = Path.Combine(tmp.Path, "fake.exe");
            File.WriteAllBytes(exe, new byte[] { 0xAB, 0xCD });
            var actualLower = BinaryIntegrityVerifier.ComputeSha256Hex(exe);

            var result = BinaryIntegrityVerifier.Check(actualLower.ToUpperInvariant(), logger, exePath: exe);

            Assert.Equal(IntegrityCheckOutcome.Match, result.Outcome);
        }

        [Fact]
        public void Check_returns_mismatch_when_hash_differs()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp);

            var exe = Path.Combine(tmp.Path, "fake.exe");
            File.WriteAllBytes(exe, new byte[] { 0x01 });
            var wrongHash = new string('f', 64);

            var result = BinaryIntegrityVerifier.Check(wrongHash, logger, exePath: exe);

            Assert.Equal(IntegrityCheckOutcome.Mismatch, result.Outcome);
            Assert.True(result.IsMismatch);
            Assert.Equal(wrongHash, result.ExpectedSha256);
            Assert.NotEqual(result.ExpectedSha256, result.ActualSha256);
        }

        [Fact]
        public void ComputeSha256Hex_matches_known_vector()
        {
            // SHA-256("abc") = ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad
            using var tmp = new TempDirectory();
            var exe = Path.Combine(tmp.Path, "abc.bin");
            File.WriteAllText(exe, "abc");

            var hash = BinaryIntegrityVerifier.ComputeSha256Hex(exe);

            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hash);
        }
    }
}
