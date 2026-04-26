using System.Collections.Generic;
using AutopilotMonitor.Agent.V2;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Program
{
    /// <summary>
    /// Codex Finding 2 regression gate: verifies that the <c>terminate_session</c> ServerAction's
    /// <c>adminOutcome</c> param is mapped onto <see cref="EnrollmentTerminationOutcome"/>
    /// instead of being silently hard-coded to <see cref="EnrollmentTerminationOutcome.Failed"/>.
    /// Portal Mark-Succeeded traffic is directly exercised here.
    /// </summary>
    public sealed class AdminActionOutcomeMappingTests
    {
        [Fact]
        public void adminOutcome_Succeeded_maps_to_Succeeded()
        {
            var result = global::AutopilotMonitor.Agent.V2.Runtime.ServerControlPlane.MapAdminOutcome(
                new Dictionary<string, string> { ["adminOutcome"] = "Succeeded" });
            Assert.Equal(EnrollmentTerminationOutcome.Succeeded, result);
        }

        [Theory]
        [InlineData("succeeded")]
        [InlineData("SUCCEEDED")]
        [InlineData("SuCcEeDeD")]
        public void adminOutcome_Succeeded_is_case_insensitive(string value)
        {
            var result = global::AutopilotMonitor.Agent.V2.Runtime.ServerControlPlane.MapAdminOutcome(
                new Dictionary<string, string> { ["adminOutcome"] = value });
            Assert.Equal(EnrollmentTerminationOutcome.Succeeded, result);
        }

        [Fact]
        public void adminOutcome_Failed_maps_to_Failed()
        {
            var result = global::AutopilotMonitor.Agent.V2.Runtime.ServerControlPlane.MapAdminOutcome(
                new Dictionary<string, string> { ["adminOutcome"] = "Failed" });
            Assert.Equal(EnrollmentTerminationOutcome.Failed, result);
        }

        [Theory]
        [InlineData("TimedOut")]
        [InlineData("weird-future-value")]
        [InlineData("")]
        public void Non_Succeeded_adminOutcome_defaults_to_Failed(string value)
        {
            // Failure-safe default: any value that isn't an explicit "Succeeded" (case-insensitive)
            // maps to Failed. This means a future outcome value the agent doesn't know yet won't
            // accidentally be treated as success.
            var result = global::AutopilotMonitor.Agent.V2.Runtime.ServerControlPlane.MapAdminOutcome(
                new Dictionary<string, string> { ["adminOutcome"] = value });
            Assert.Equal(EnrollmentTerminationOutcome.Failed, result);
        }

        [Fact]
        public void Missing_adminOutcome_param_maps_to_Failed()
        {
            // A kill-signal-driven terminate_session sets origin=kill_signal but no adminOutcome —
            // that's an expected Failed path (hard termination).
            var result = global::AutopilotMonitor.Agent.V2.Runtime.ServerControlPlane.MapAdminOutcome(
                new Dictionary<string, string> { ["origin"] = "kill_signal" });
            Assert.Equal(EnrollmentTerminationOutcome.Failed, result);
        }

        [Fact]
        public void Null_params_dictionary_maps_to_Failed()
        {
            var result = global::AutopilotMonitor.Agent.V2.Runtime.ServerControlPlane.MapAdminOutcome(null);
            Assert.Equal(EnrollmentTerminationOutcome.Failed, result);
        }
    }
}
