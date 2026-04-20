using System;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Signals;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.SignalAdapters
{
    public sealed class AadJoinWatcherAdapterTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        private sealed class Fixture : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public AgentLogger Logger { get; }
            public AadJoinWatcher Watcher { get; }
            public FakeSignalIngressSink Ingress { get; } = new FakeSignalIngressSink();
            public VirtualClock Clock { get; } = new VirtualClock(Fixed);

            public Fixture()
            {
                Logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
                Watcher = new AadJoinWatcher(Logger);
            }

            public void Dispose()
            {
                Watcher.Dispose();
                Tmp.Dispose();
            }
        }

        [Fact]
        public void TriggerFromTest_emits_AadUserJoinedLate_with_domain_not_full_email()
        {
            using var f = new Fixture();
            using var adapter = new AadJoinWatcherAdapter(f.Watcher, f.Ingress, f.Clock);

            adapter.TriggerFromTest("alice@contoso.com", "abcd1234");

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.AadUserJoinedLate, posted.Kind);
            Assert.Equal("AadJoinWatcher", posted.SourceOrigin);
            Assert.Equal(EvidenceKind.Derived, posted.Evidence.Kind);
            Assert.Equal("contoso.com", posted.Payload!["userDomain"]);
            Assert.Equal("true", posted.Payload["hasThumbprint"]);

            // PII guard: full email MUST NOT appear in payload.
            Assert.DoesNotContain("alice@", string.Concat(posted.Payload.Values));
        }

        [Fact]
        public void Part2Mode_emits_UserAadSignInComplete_kind()
        {
            using var f = new Fixture();
            using var adapter = new AadJoinWatcherAdapter(f.Watcher, f.Ingress, f.Clock, part2Mode: true);

            adapter.TriggerFromTest("bob@tailspin.com", "x");

            var posted = f.Ingress.Posted.Single();
            Assert.Equal(DecisionSignalKind.UserAadSignInComplete, posted.Kind);
            Assert.Contains("Post-reboot", posted.Evidence.Summary);
        }

        [Fact]
        public void Empty_thumbprint_is_reflected_in_payload()
        {
            using var f = new Fixture();
            using var adapter = new AadJoinWatcherAdapter(f.Watcher, f.Ingress, f.Clock);

            adapter.TriggerFromTest("user@example.org", "");

            Assert.Equal("false", f.Ingress.Posted[0].Payload!["hasThumbprint"]);
        }

        [Fact]
        public void Malformed_email_falls_back_to_unknown_domain()
        {
            using var f = new Fixture();
            using var adapter = new AadJoinWatcherAdapter(f.Watcher, f.Ingress, f.Clock);

            adapter.TriggerFromTest("no-at-sign", "x");

            Assert.Equal("unknown", f.Ingress.Posted[0].Payload!["userDomain"]);
        }

        [Fact]
        public void Duplicate_trigger_is_deduplicated()
        {
            using var f = new Fixture();
            using var adapter = new AadJoinWatcherAdapter(f.Watcher, f.Ingress, f.Clock);

            adapter.TriggerFromTest("alice@contoso.com", "x");
            adapter.TriggerFromTest("alice@contoso.com", "x");

            Assert.Single(f.Ingress.Posted);
        }

        [Fact]
        public void Ctor_null_args_throw()
        {
            using var f = new Fixture();
            Assert.Throws<ArgumentNullException>(() => new AadJoinWatcherAdapter(null!, f.Ingress, f.Clock));
            Assert.Throws<ArgumentNullException>(() => new AadJoinWatcherAdapter(f.Watcher, null!, f.Clock));
            Assert.Throws<ArgumentNullException>(() => new AadJoinWatcherAdapter(f.Watcher, f.Ingress, null!));
        }
    }
}
