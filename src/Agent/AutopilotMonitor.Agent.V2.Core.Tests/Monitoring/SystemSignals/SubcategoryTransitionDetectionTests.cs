using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
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
    /// Regression coverage for the previously-unreachable non-failure branch in
    /// <c>DetectSubcategoryTransitions</c>. The branch was guarded by an inner
    /// <c>if (isFailure)</c> that is always false once the outer
    /// <c>isFailure || isCompletion</c> has been excluded, so every non-failure,
    /// non-completion, non-noise transition (e.g. <c>succeeded -&gt; in_progress</c> retry)
    /// was silently dropped — the <c>esp_provisioning_status</c> event downgraded to a
    /// plain progress update without a <c>subcategory_state_change</c> changeType.
    /// After the fix those transitions must surface in the event.
    /// </summary>
    public sealed class SubcategoryTransitionDetectionTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 6, 5, 9, 0, 0, DateTimeKind.Utc);

        private sealed class Fixture : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public FakeSignalIngressSink Sink { get; } = new FakeSignalIngressSink();
            public ProvisioningStatusTracker Tracker { get; }

            public Fixture()
            {
                var clock = new VirtualClock(Fixed);
                var post = new InformationalEventPost(Sink, clock);
                var logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
                Tracker = new ProvisioningStatusTracker(
                    sessionId: "S1",
                    tenantId: "T1",
                    post: post,
                    logger: logger);
            }

            public IReadOnlyList<(string EventType, IReadOnlyDictionary<string, object> Data)> CapturedEvents()
            {
                return Sink.Posted
                    .Where(p => p.Kind == DecisionSignalKind.InformationalEvent && p.Payload != null)
                    .Select(p =>
                    {
                        var eventType = p.Payload!.TryGetValue(SignalPayloadKeys.EventType, out var et) ? et : "";
                        var data = p.TypedPayload as IReadOnlyDictionary<string, object>
                                   ?? (IReadOnlyDictionary<string, object>)new Dictionary<string, object>();
                        return (eventType, data);
                    })
                    .ToList();
            }

            public void Dispose()
            {
                Tracker.Dispose();
                Tmp.Dispose();
            }
        }

        [Fact]
        public void NonFailureSubcategoryTransition_IsRecorded_AndSurfacedAsStateChange()
        {
            using var f = new Fixture();

            // First seen — Apps already succeeded.
            f.Tracker.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", @"{
                ""categorySucceeded"": null,
                ""AppsSubcategory"": {""subcategoryState"":""succeeded"",""subcategoryStatusText"":""Apps (Identified)""}
            }");

            // Apps regresses succeeded -> in_progress (retry). categorySucceeded unchanged (null),
            // so processing falls through to subcategory-transition detection.
            f.Tracker.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", @"{
                ""categorySucceeded"": null,
                ""AppsSubcategory"": {""subcategoryState"":""in_progress"",""subcategoryStatusText"":""Apps (Downloading)""}
            }");

            var captured = f.CapturedEvents();
            var statusEvents = captured
                .Where(e => e.EventType == AutopilotMonitor.Shared.Constants.EventTypes.EspProvisioningStatus)
                .ToList();

            // A subcategory_state_change event must be emitted carrying the transition.
            var transitionEvent = statusEvents.Single(e =>
                e.Data.TryGetValue("changeType", out var ct) && (string)ct == "subcategory_state_change");

            var transitions = Assert.IsType<List<Dictionary<string, string>>>(transitionEvent.Data["transitions"]);
            var appsTransition = Assert.Single(transitions, t => t["subcategory"] == "Apps");
            Assert.Equal("succeeded", appsTransition["previousState"]);
            Assert.Equal("in_progress", appsTransition["newState"]);

            // Non-failure transition: no failed-subcategory enrichment.
            Assert.False(transitionEvent.Data.ContainsKey("failedSubcategories"));
        }

        [Fact]
        public void NotStartedToInProgress_IsTreatedAsNoise_NotRecorded()
        {
            using var f = new Fixture();

            f.Tracker.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", @"{
                ""categorySucceeded"": null,
                ""AppsSubcategory"": {""subcategoryState"":""notStarted"",""subcategoryStatusText"":""Apps (Waiting)""}
            }");

            f.Tracker.ProcessCategoryStatusForTest("DeviceSetupCategory.Status", @"{
                ""categorySucceeded"": null,
                ""AppsSubcategory"": {""subcategoryState"":""in_progress"",""subcategoryStatusText"":""Apps (Downloading)""}
            }");

            var captured = f.CapturedEvents();
            var statusEvents = captured
                .Where(e => e.EventType == AutopilotMonitor.Shared.Constants.EventTypes.EspProvisioningStatus)
                .ToList();

            // notStarted -> in_progress is benign progress noise: no state-change event.
            Assert.DoesNotContain(statusEvents, e =>
                e.Data.TryGetValue("changeType", out var ct) && (string)ct == "subcategory_state_change");
        }
    }
}
