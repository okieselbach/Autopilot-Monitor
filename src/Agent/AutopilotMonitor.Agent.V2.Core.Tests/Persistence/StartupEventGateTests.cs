#nullable enable
using System.Collections.Generic;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Persistence;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Persistence
{
    /// <summary>
    /// StartupEventGate — the cross-cutting restart dedup for one-shot startup checks.
    /// Covers both policies (emit-on-change, retry-until-success), cross-instance persistence
    /// (a new instance over the same state directory models an agent restart), fingerprint
    /// stability and fail-soft behavior on corrupt state.
    /// </summary>
    public sealed class StartupEventGateTests
    {
        private static AgentLogger NewLogger(string dir) => new AgentLogger(dir, AgentLogLevel.Info);

        // ---------------------------------------------------------------- emit-on-change

        [Fact]
        public void ShouldEmit_true_first_time_false_for_same_fingerprint_true_on_change()
        {
            using var tmp = new TempDirectory();
            var gate = new StartupEventGate(tmp.Path, NewLogger(tmp.Path));

            Assert.True(gate.ShouldEmit("os_info", "fp-1"));
            Assert.False(gate.ShouldEmit("os_info", "fp-1")); // identical repeat suppressed
            Assert.True(gate.ShouldEmit("os_info", "fp-2"));  // real change re-emits
            Assert.False(gate.ShouldEmit("os_info", "fp-2"));
        }

        [Fact]
        public void ShouldEmit_state_survives_an_agent_restart()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);

            var firstRun = new StartupEventGate(tmp.Path, logger);
            Assert.True(firstRun.ShouldEmit("aad_join_status", "not-joined"));

            // New instance over the same state directory = agent restarted after a reboot.
            var secondRun = new StartupEventGate(tmp.Path, logger);
            Assert.False(secondRun.ShouldEmit("aad_join_status", "not-joined")); // unchanged → suppressed
            Assert.True(secondRun.ShouldEmit("aad_join_status", "joined"));      // late join → emits
        }

        [Fact]
        public void Keys_are_independent()
        {
            using var tmp = new TempDirectory();
            var gate = new StartupEventGate(tmp.Path, NewLogger(tmp.Path));

            Assert.True(gate.ShouldEmit("hardware_spec", "fp"));
            Assert.True(gate.ShouldEmit("tpm_status", "fp")); // same fingerprint, different key
        }

        // ---------------------------------------------------------------- retry-until-success

        [Fact]
        public void MarkSucceeded_latches_across_restarts()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);

            var firstRun = new StartupEventGate(tmp.Path, logger);
            Assert.False(firstRun.AlreadySucceeded("device_location"));
            firstRun.MarkSucceeded("device_location");
            Assert.True(firstRun.AlreadySucceeded("device_location"));

            var secondRun = new StartupEventGate(tmp.Path, logger);
            Assert.True(secondRun.AlreadySucceeded("device_location"));
            Assert.False(secondRun.AlreadySucceeded("ntp_time_check")); // never marked → retries
        }

        [Fact]
        public void ShouldEmit_preserves_a_previous_success_latch_and_vice_versa()
        {
            using var tmp = new TempDirectory();
            var gate = new StartupEventGate(tmp.Path, NewLogger(tmp.Path));

            gate.MarkSucceeded("k");
            Assert.True(gate.ShouldEmit("k", "fp-1")); // fingerprint update...
            Assert.True(gate.AlreadySucceeded("k"));   // ...must not clear the success latch

            gate.MarkSucceeded("k");                   // success latch update...
            Assert.False(gate.ShouldEmit("k", "fp-1")); // ...must not clear the fingerprint
        }

        // ---------------------------------------------------------------- fail-soft

        [Theory]
        [InlineData("not json {{{")]
        [InlineData("null")]
        public void Corrupt_state_file_loads_fresh_and_everything_emits(string content)
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var gate = new StartupEventGate(tmp.Path, logger);
            File.WriteAllText(gate.StateFilePath, content);

            var reloaded = new StartupEventGate(tmp.Path, logger);
            Assert.True(reloaded.ShouldEmit("os_info", "fp"));
            Assert.False(reloaded.AlreadySucceeded("device_location"));
        }

        // ---------------------------------------------------------------- fingerprint

        [Fact]
        public void ComputeFingerprint_is_stable_across_toplevel_insertion_order()
        {
            var a = new Dictionary<string, object> { { "x", 1 }, { "y", "two" } };
            var b = new Dictionary<string, object> { { "y", "two" }, { "x", 1 } };

            Assert.Equal(StartupEventGate.ComputeFingerprint(a), StartupEventGate.ComputeFingerprint(b));
        }

        [Fact]
        public void ComputeFingerprint_changes_when_a_value_changes()
        {
            var a = new Dictionary<string, object> { { "joinType", "Not Joined" } };
            var b = new Dictionary<string, object> { { "joinType", "Azure AD Joined" } };

            Assert.NotEqual(StartupEventGate.ComputeFingerprint(a), StartupEventGate.ComputeFingerprint(b));
        }

        [Fact]
        public void ComputeFingerprint_ignores_excluded_volatile_fields()
        {
            var a = new Dictionary<string, object> { { "adapterName", "WiFi" }, { "linkSpeedMbps", 433L } };
            var b = new Dictionary<string, object> { { "adapterName", "WiFi" }, { "linkSpeedMbps", 866L } };
            var excluded = new[] { "linkSpeedMbps" };

            Assert.Equal(
                StartupEventGate.ComputeFingerprint(a, excluded),
                StartupEventGate.ComputeFingerprint(b, excluded));
            Assert.NotEqual(
                StartupEventGate.ComputeFingerprint(a),
                StartupEventGate.ComputeFingerprint(b));
        }

        [Fact]
        public void ComputeFingerprint_handles_nested_structures()
        {
            var a = new Dictionary<string, object>
            {
                { "adapters", new List<Dictionary<string, object>> { new Dictionary<string, object> { { "mac", "AA" } } } },
            };
            var b = new Dictionary<string, object>
            {
                { "adapters", new List<Dictionary<string, object>> { new Dictionary<string, object> { { "mac", "BB" } } } },
            };

            Assert.NotEqual(StartupEventGate.ComputeFingerprint(a), StartupEventGate.ComputeFingerprint(b));
        }
    }
}
