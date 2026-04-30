using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests
{
    /// <summary>
    /// Tests for <see cref="AdminConfiguration.GetModernDeploymentHarmlessEventIds"/>.
    /// Pipeline: backend stores the raw JSON string → helper returns a safe list with
    /// sensible fallbacks so the agent always receives a usable default.
    /// </summary>
    public sealed class AdminConfigurationHarmlessEventIdsTests
    {
        [Fact]
        public void GetHarmlessEventIds_NullJson_ReturnsDefaults()
        {
            var cfg = new AdminConfiguration();

            var ids = cfg.GetModernDeploymentHarmlessEventIds();

            Assert.Equal(new[] { 100, 1005, 1010 }, ids);
        }

        [Fact]
        public void GetHarmlessEventIds_EmptyJson_ReturnsDefaults()
        {
            var cfg = new AdminConfiguration { ModernDeploymentHarmlessEventIdsJson = "   " };

            var ids = cfg.GetModernDeploymentHarmlessEventIds();

            Assert.Equal(new[] { 100, 1005, 1010 }, ids);
        }

        [Fact]
        public void GetHarmlessEventIds_InvalidJson_ReturnsDefaults()
        {
            var cfg = new AdminConfiguration { ModernDeploymentHarmlessEventIdsJson = "not-json" };

            var ids = cfg.GetModernDeploymentHarmlessEventIds();

            Assert.Equal(new[] { 100, 1005, 1010 }, ids);
        }

        [Fact]
        public void GetHarmlessEventIds_ValidJson_RoundTrips()
        {
            var cfg = new AdminConfiguration { ModernDeploymentHarmlessEventIdsJson = "[100,1005,4242]" };

            var ids = cfg.GetModernDeploymentHarmlessEventIds();

            Assert.Equal(new[] { 100, 1005, 4242 }, ids);
        }

        [Fact]
        public void GetHarmlessEventIds_EmptyArray_ReturnsEmptyList()
        {
            // Explicit empty array means "no suppression" — not a fallback trigger.
            var cfg = new AdminConfiguration { ModernDeploymentHarmlessEventIdsJson = "[]" };

            var ids = cfg.GetModernDeploymentHarmlessEventIds();

            Assert.Empty(ids);
        }
    }
}
