using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.SystemSignals
{
    /// <summary>
    /// Review MON-D1: a watcher that fails to arm must surface a one-shot <c>collector_degraded</c>
    /// Warning so the backend can tell a dead kernel watcher apart from a genuine no-signal session.
    /// </summary>
    public sealed class CollectorDegradationReporterTests
    {
        private static readonly DateTime At = new DateTime(2026, 6, 10, 9, 0, 0, DateTimeKind.Utc);

        private static (InformationalEventPost post, FakeSignalIngressSink sink) BuildRig()
        {
            var sink = new FakeSignalIngressSink();
            var post = new InformationalEventPost(sink, new VirtualClock(At));
            return (post, sink);
        }

        [Fact]
        public void Report_emits_warning_collector_degraded_with_collector_and_error_detail()
        {
            var (post, sink) = BuildRig();

            CollectorDegradationReporter.Report(
                post, sessionId: "s1", tenantId: "t1",
                collectorName: "ShellCoreTracker",
                reason: "watcher_arm_failed",
                ex: new InvalidOperationException("boom"));

            var posted = Assert.Single(sink.Posted);
            Assert.Equal(DecisionSignalKind.InformationalEvent, posted.Kind);
            Assert.Equal("collector_degraded", posted.Payload![SignalPayloadKeys.EventType]);
            Assert.Equal("ShellCoreTracker", posted.Payload[SignalPayloadKeys.Source]);
            Assert.Equal("Warning", posted.Payload[SignalPayloadKeys.Severity]);

            var data = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(posted.TypedPayload!);
            Assert.Equal("ShellCoreTracker", data["collector"]);
            Assert.Equal("watcher_arm_failed", data["reason"]);
            Assert.Equal("boom", data["error"]);
            Assert.Equal(nameof(InvalidOperationException), data["errorType"]);
        }

        [Fact]
        public void Report_without_exception_omits_error_fields()
        {
            var (post, sink) = BuildRig();

            CollectorDegradationReporter.Report(
                post, "s1", "t1", "ModernDeploymentTracker", "watcher_arm_failed:SomeChannel");

            var posted = Assert.Single(sink.Posted);
            var data = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(posted.TypedPayload!);
            Assert.Equal("ModernDeploymentTracker", data["collector"]);
            Assert.Equal("watcher_arm_failed:SomeChannel", data["reason"]);
            Assert.False(data.ContainsKey("error"));
            Assert.False(data.ContainsKey("errorType"));
        }

        [Fact]
        public void Report_with_null_post_is_a_noop_and_does_not_throw()
        {
            CollectorDegradationReporter.Report(null, "s1", "t1", "X", "y");
        }
    }
}
