using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests
{
    /// <summary>
    /// Tests for <see cref="AdminConfiguration.GetWhiteGloveSealingPatternIds"/>. Unlike the
    /// ModernDeployment helper, this feature is <b>off by default</b> — the fallback for
    /// null / empty / invalid JSON must be an empty list so M3-compatible behaviour is
    /// preserved until an operator opts in.
    /// </summary>
    public sealed class AdminConfigurationWhiteGloveSealingPatternIdsTests
    {
        [Fact]
        public void GetWhiteGloveSealingPatternIds_NullJson_ReturnsEmpty()
        {
            var cfg = new AdminConfiguration();

            var ids = cfg.GetWhiteGloveSealingPatternIds();

            Assert.Empty(ids);
        }

        [Fact]
        public void GetWhiteGloveSealingPatternIds_WhitespaceJson_ReturnsEmpty()
        {
            var cfg = new AdminConfiguration { WhiteGloveSealingPatternIdsJson = "   " };

            var ids = cfg.GetWhiteGloveSealingPatternIds();

            Assert.Empty(ids);
        }

        [Fact]
        public void GetWhiteGloveSealingPatternIds_InvalidJson_ReturnsEmpty()
        {
            var cfg = new AdminConfiguration { WhiteGloveSealingPatternIdsJson = "not-json" };

            var ids = cfg.GetWhiteGloveSealingPatternIds();

            Assert.Empty(ids);
        }

        [Fact]
        public void GetWhiteGloveSealingPatternIds_ValidJson_RoundTrips()
        {
            var cfg = new AdminConfiguration
            {
                WhiteGloveSealingPatternIdsJson = "[\"wg-seal-1\",\"wg-seal-2\",\"wg-seal-3\"]"
            };

            var ids = cfg.GetWhiteGloveSealingPatternIds();

            Assert.Equal(new[] { "wg-seal-1", "wg-seal-2", "wg-seal-3" }, ids);
        }

        [Fact]
        public void GetWhiteGloveSealingPatternIds_EmptyArray_ReturnsEmptyList()
        {
            // Explicit empty array means "feature opted-in but no patterns yet" — same
            // observable behaviour as the default but distinguishable in storage.
            var cfg = new AdminConfiguration { WhiteGloveSealingPatternIdsJson = "[]" };

            var ids = cfg.GetWhiteGloveSealingPatternIds();

            Assert.Empty(ids);
        }
    }
}
