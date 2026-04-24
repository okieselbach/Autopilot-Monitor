using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.DecisionCore.Classifiers;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    public sealed class EffectRunnerTests
    {
        private static DateTime At => new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        private static DecisionState InitialState() =>
            DecisionState.CreateInitial("S1", "T1");

        private static ActiveDeadline Deadline(string name, DateTime dueAt) =>
            new ActiveDeadline(name, dueAt, DecisionSignalKind.DeadlineFired);

        private sealed class Rig
        {
            public FakeDeadlineScheduler Scheduler { get; } = new FakeDeadlineScheduler();
            public FakeSnapshotPersistence Snapshot { get; } = new FakeSnapshotPersistence();
            public FakeSignalIngressSink Ingress { get; } = new FakeSignalIngressSink();
            public FakeEventTimelineEmitter Emitter { get; } = new FakeEventTimelineEmitter();
            public Dictionary<string, IClassifier> Classifiers { get; } = new Dictionary<string, IClassifier>();
            public VirtualClock Clock { get; } = new VirtualClock(At);

            public IEffectRunner Build() =>
                new EffectRunner(
                    scheduler: Scheduler,
                    classifiers: new ClassifierRegistry(Classifiers.Values),
                    ingress: Ingress,
                    emitter: Emitter,
                    snapshot: Snapshot,
                    clock: Clock);
        }

        // ----- ScheduleDeadline / CancelDeadline (critical class) -----

        [Fact]
        public async Task ScheduleDeadline_forwards_to_scheduler()
        {
            var rig = new Rig();
            var sut = rig.Build();
            var d = Deadline("hello_safety", At.AddMinutes(5));
            var effect = new DecisionEffect(DecisionEffectKind.ScheduleDeadline, deadline: d);

            var result = await sut.RunAsync(new[] { effect }, InitialState(), At);

            Assert.True(result.Success);
            Assert.Single(rig.Scheduler.Scheduled);
            Assert.Equal("hello_safety", rig.Scheduler.Scheduled[0].Name);
        }

        [Fact]
        public async Task CancelDeadline_forwards_to_scheduler()
        {
            var rig = new Rig();
            var sut = rig.Build();
            var effect = new DecisionEffect(DecisionEffectKind.CancelDeadline, cancelDeadlineName: "hello_safety");

            var result = await sut.RunAsync(new[] { effect }, InitialState(), At);

            Assert.True(result.Success);
            Assert.Single(rig.Scheduler.Cancelled);
            Assert.Equal("hello_safety", rig.Scheduler.Cancelled[0]);
        }

        [Fact]
        public async Task ScheduleDeadline_exception_aborts_session_with_timer_infrastructure_failure()
        {
            var rig = new Rig();
            rig.Scheduler.ThrowOnSchedule = new InvalidOperationException("timer-broken");
            var sut = rig.Build();
            var effect = new DecisionEffect(
                DecisionEffectKind.ScheduleDeadline,
                deadline: Deadline("x", At.AddMinutes(1)));

            var result = await sut.RunAsync(new[] { effect }, InitialState(), At);

            Assert.True(result.SessionMustAbort);
            Assert.Contains("timer_infrastructure_failure", result.AbortReason);
            Assert.Single(result.Failures);
            Assert.Equal(DecisionEffectKind.ScheduleDeadline, result.Failures[0].EffectKind);
            Assert.False(result.Failures[0].IsTransient);
        }

        [Fact]
        public async Task ScheduleDeadline_failure_posts_EffectInfrastructureFailure_signal_to_ingress()
        {
            // Codex follow-up #2 — critical deadline failure must surface in the decision
            // world, not just the log. EffectRunner posts a synthetic signal that the
            // reducer picks up on the next step.
            var rig = new Rig();
            rig.Scheduler.ThrowOnSchedule = new InvalidOperationException("timer-broken");
            var sut = rig.Build();
            var effect = new DecisionEffect(
                DecisionEffectKind.ScheduleDeadline,
                deadline: Deadline("hello_safety", At.AddMinutes(1)));

            var result = await sut.RunAsync(new[] { effect }, InitialState(), At);

            Assert.True(result.SessionMustAbort);
            Assert.Single(rig.Ingress.Posted);
            var posted = rig.Ingress.Posted[0];
            Assert.Equal(DecisionSignalKind.EffectInfrastructureFailure, posted.Kind);
            Assert.Equal(At, posted.OccurredAtUtc);
            Assert.Equal("effectrunner:critical:ScheduleDeadline", posted.SourceOrigin);
            Assert.NotNull(posted.Payload);
            Assert.Equal(result.AbortReason, posted.Payload!["reason"]);
            Assert.Equal("ScheduleDeadline", posted.Payload["failingEffect"]);
        }

        [Fact]
        public async Task CancelDeadline_failure_posts_EffectInfrastructureFailure_signal_to_ingress()
        {
            var rig = new Rig();
            rig.Scheduler.ThrowOnCancel = new InvalidOperationException("timer-broken");
            var sut = rig.Build();
            var effect = new DecisionEffect(
                DecisionEffectKind.CancelDeadline,
                cancelDeadlineName: "hello_safety");

            var result = await sut.RunAsync(new[] { effect }, InitialState(), At);

            Assert.True(result.SessionMustAbort);
            Assert.Single(rig.Ingress.Posted);
            var posted = rig.Ingress.Posted[0];
            Assert.Equal(DecisionSignalKind.EffectInfrastructureFailure, posted.Kind);
            Assert.Equal("effectrunner:critical:CancelDeadline", posted.SourceOrigin);
            Assert.Equal("CancelDeadline", posted.Payload!["failingEffect"]);
        }

        [Fact]
        public async Task Critical_failure_post_tolerates_ingress_exceptions()
        {
            // The max-lifetime watchdog is the last-resort safety net — if the synthetic
            // post itself throws (e.g. channel closed), the EffectRunner must still
            // return cleanly rather than crashing the ingress worker.
            var rig = new Rig();
            rig.Scheduler.ThrowOnSchedule = new InvalidOperationException("timer-broken");
            rig.Ingress.ThrowOnPost = new InvalidOperationException("ingress-full");
            var sut = rig.Build();
            var effect = new DecisionEffect(
                DecisionEffectKind.ScheduleDeadline,
                deadline: Deadline("x", At.AddMinutes(1)));

            var result = await sut.RunAsync(new[] { effect }, InitialState(), At);

            Assert.True(result.SessionMustAbort);
            // Ingress-Post throw was swallowed — no unhandled exception here.
        }

        [Fact]
        public async Task ScheduleDeadline_abort_stops_processing_subsequent_effects()
        {
            var rig = new Rig();
            rig.Scheduler.ThrowOnSchedule = new InvalidOperationException("timer-broken");
            var sut = rig.Build();

            var effects = new[]
            {
                new DecisionEffect(DecisionEffectKind.ScheduleDeadline, deadline: Deadline("x", At.AddMinutes(1))),
                new DecisionEffect(DecisionEffectKind.EmitEventTimelineEntry, parameters: new Dictionary<string, string> { ["eventType"] = "won't-fire" }),
                new DecisionEffect(DecisionEffectKind.PersistSnapshot),
            };

            var result = await sut.RunAsync(effects, InitialState(), At);

            Assert.True(result.SessionMustAbort);
            Assert.Equal(0, rig.Emitter.CallCount);   // dispatching stopped
            Assert.Empty(rig.Snapshot.Saved);
        }

        [Fact]
        public async Task ScheduleDeadline_missing_Deadline_payload_aborts()
        {
            var rig = new Rig();
            var sut = rig.Build();
            var effect = new DecisionEffect(DecisionEffectKind.ScheduleDeadline);

            var result = await sut.RunAsync(new[] { effect }, InitialState(), At);

            Assert.True(result.SessionMustAbort);
        }

        // ----- EmitEventTimelineEntry (transient class) -----

        [Fact]
        public async Task EmitEventTimelineEntry_forwards_parameters_and_state()
        {
            var rig = new Rig();
            var sut = rig.Build();
            var parameters = new Dictionary<string, string> { ["eventType"] = "enrollment_complete" };
            var effect = new DecisionEffect(DecisionEffectKind.EmitEventTimelineEntry, parameters: parameters);

            var result = await sut.RunAsync(new[] { effect }, InitialState(), At);

            Assert.True(result.Success);
            Assert.Equal(1, rig.Emitter.CallCount);
            Assert.Equal("enrollment_complete", rig.Emitter.Calls[0].Parameters!["eventType"]);
            Assert.Equal("S1", rig.Emitter.Calls[0].State.SessionId);
        }

        [Fact]
        public async Task EmitEventTimelineEntry_retries_on_transient_failure_up_to_3_times()
        {
            var rig = new Rig();
            rig.Emitter.ScriptThrow(new InvalidOperationException("hiccup"), count: 2).ScriptOk();
            var sut = rig.Build();
            var effect = new DecisionEffect(DecisionEffectKind.EmitEventTimelineEntry);

            var result = await sut.RunAsync(new[] { effect }, InitialState(), At);

            Assert.True(result.Success);
            Assert.Equal(3, rig.Emitter.CallCount);   // 2 fails + 1 success
        }

        [Fact]
        public async Task EmitEventTimelineEntry_transient_exhaust_records_failure_without_aborting()
        {
            // Plan §2.7b: transient exhaust → Warning-Log, State unverändert, weiter im Flow.
            var rig = new Rig();
            rig.Emitter.ScriptThrow(new InvalidOperationException("persistent-hiccup"), count: 4);
            var sut = rig.Build();
            var effect = new DecisionEffect(DecisionEffectKind.EmitEventTimelineEntry);

            var result = await sut.RunAsync(new[] { effect }, InitialState(), At);

            Assert.False(result.SessionMustAbort);
            Assert.Single(result.Failures);
            Assert.True(result.Failures[0].IsTransient);
            Assert.True(result.Failures[0].ExhaustedRetries);
            Assert.Equal(4, rig.Emitter.CallCount);  // 1 initial + 3 retries
        }

        [Fact]
        public async Task EmitEventTimelineEntry_failure_does_not_block_subsequent_effects()
        {
            var rig = new Rig();
            rig.Emitter.ScriptThrow(new InvalidOperationException("bad"), count: 4);
            var sut = rig.Build();

            var effects = new[]
            {
                new DecisionEffect(DecisionEffectKind.EmitEventTimelineEntry),
                new DecisionEffect(DecisionEffectKind.PersistSnapshot),
            };

            var result = await sut.RunAsync(effects, InitialState(), At);

            Assert.False(result.SessionMustAbort);
            Assert.Single(result.Failures);
            Assert.Single(rig.Snapshot.Saved);
        }

        // ----- PersistSnapshot (transient class) -----

        [Fact]
        public async Task PersistSnapshot_saves_current_state()
        {
            var rig = new Rig();
            var sut = rig.Build();
            var effect = new DecisionEffect(DecisionEffectKind.PersistSnapshot);

            var result = await sut.RunAsync(new[] { effect }, InitialState(), At);

            Assert.True(result.Success);
            Assert.Single(rig.Snapshot.Saved);
            Assert.Equal("S1", rig.Snapshot.Saved[0].SessionId);
        }

        [Fact]
        public async Task PersistSnapshot_retries_on_transient_failure()
        {
            var rig = new Rig();
            rig.Snapshot.ScriptThrow(new IOException_("disk-full"), count: 1).ScriptOk();
            var sut = rig.Build();
            var effect = new DecisionEffect(DecisionEffectKind.PersistSnapshot);

            var result = await sut.RunAsync(new[] { effect }, InitialState(), At);

            Assert.True(result.Success);
            Assert.Equal(2, rig.Snapshot.SaveCallCount);
        }

        // ----- RunClassifier (optional class) -----

        [Fact]
        public async Task RunClassifier_happy_invokes_classifier_and_posts_verdict_signal()
        {
            var rig = new Rig();
            const string Id = WhiteGloveSealingClassifier.ClassifierId;
            const string Hash = "hash-abc123";
            rig.Classifiers[Id] = new FakeClassifier(
                Id,
                _ => FakeClassifier.Verdict(Id, HypothesisLevel.Strong, Hash, score: 85));

            var sut = rig.Build();
            var effect = new DecisionEffect(
                DecisionEffectKind.RunClassifier,
                classifierId: Id,
                classifierSnapshot: new HashSnapshot(Hash));

            var result = await sut.RunAsync(new[] { effect }, InitialState(), At);

            Assert.True(result.Success);
            Assert.Equal(1, result.ClassifierInvocations);
            Assert.Equal(0, result.ClassifierSkippedByAntiLoop);
            Assert.Single(rig.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.ClassifierVerdictIssued, rig.Ingress.Posted[0].Kind);
            Assert.Equal(At, rig.Ingress.Posted[0].OccurredAtUtc);
            var payload = rig.Ingress.Posted[0].Payload!;
            Assert.Equal("Strong", payload["level"]);
            Assert.Equal(Hash, payload["inputHash"]);
            Assert.Equal(EvidenceKind.Synthetic, rig.Ingress.Posted[0].Evidence.Kind);
        }

        [Fact]
        public async Task RunClassifier_antiloop_skips_when_snapshot_hash_matches_prior_verdict()
        {
            // Seed state with a hypothesis whose LastClassifierVerdictId equals the snapshot hash.
            const string Id = WhiteGloveSealingClassifier.ClassifierId;
            const string Hash = "prior-hash";

            var state = InitialState()
                .ToBuilder()
                .Apply(b => b.WhiteGloveSealing = new Hypothesis(
                    level: HypothesisLevel.Strong,
                    reason: "seed",
                    score: 80,
                    lastUpdatedUtc: At,
                    lastClassifierVerdictId: Hash))
                .Build();

            var rig = new Rig();
            var invoked = false;
            rig.Classifiers[Id] = new FakeClassifier(Id, _ =>
            {
                invoked = true;
                return FakeClassifier.Verdict(Id, HypothesisLevel.Strong, Hash);
            });

            var sut = rig.Build();
            var effect = new DecisionEffect(
                DecisionEffectKind.RunClassifier,
                classifierId: Id,
                classifierSnapshot: new HashSnapshot(Hash));

            var result = await sut.RunAsync(new[] { effect }, state, At);

            Assert.True(result.Success);
            Assert.False(invoked);   // classifier not called
            Assert.Equal(0, result.ClassifierInvocations);
            Assert.Equal(1, result.ClassifierSkippedByAntiLoop);
            Assert.Empty(rig.Ingress.Posted);
        }

        [Fact]
        public async Task RunClassifier_exception_posts_Inconclusive_verdict_without_aborting()
        {
            var rig = new Rig();
            const string Id = WhiteGloveSealingClassifier.ClassifierId;
            rig.Classifiers[Id] = new FakeClassifier(Id, _ => throw new InvalidOperationException("bug"));

            var sut = rig.Build();
            var effect = new DecisionEffect(
                DecisionEffectKind.RunClassifier,
                classifierId: Id,
                classifierSnapshot: new HashSnapshot("h1"));

            var result = await sut.RunAsync(new[] { effect }, InitialState(), At);

            Assert.False(result.SessionMustAbort);
            Assert.Single(result.Failures);
            Assert.Single(rig.Ingress.Posted);
            var payload = rig.Ingress.Posted[0].Payload!;
            Assert.Equal("Inconclusive", payload["level"]);
            Assert.Contains("exception", payload["reason"]);
        }

        [Fact]
        public async Task RunClassifier_unknown_id_records_failure_without_aborting()
        {
            var rig = new Rig();
            var sut = rig.Build();
            var effect = new DecisionEffect(
                DecisionEffectKind.RunClassifier,
                classifierId: "never-registered",
                classifierSnapshot: new HashSnapshot("h"));

            var result = await sut.RunAsync(new[] { effect }, InitialState(), At);

            Assert.False(result.SessionMustAbort);
            Assert.Single(result.Failures);
            Assert.Contains("never-registered", result.Failures[0].ErrorReason);
            Assert.Empty(rig.Ingress.Posted);
        }

        [Fact]
        public async Task RunClassifier_missing_snapshot_records_failure()
        {
            var rig = new Rig();
            const string Id = WhiteGloveSealingClassifier.ClassifierId;
            rig.Classifiers[Id] = new FakeClassifier(Id, _ => FakeClassifier.Verdict(Id, HypothesisLevel.Weak, "x"));
            var sut = rig.Build();
            var effect = new DecisionEffect(DecisionEffectKind.RunClassifier, classifierId: Id);

            var result = await sut.RunAsync(new[] { effect }, InitialState(), At);

            Assert.Single(result.Failures);
            Assert.False(result.SessionMustAbort);
        }

        // ----- Batches and edge cases -----

        [Fact]
        public async Task Multiple_effects_in_one_batch_dispatch_in_order()
        {
            var rig = new Rig();
            const string Id = WhiteGloveSealingClassifier.ClassifierId;
            rig.Classifiers[Id] = new FakeClassifier(Id, _ => FakeClassifier.Verdict(Id, HypothesisLevel.Strong, "h"));
            var sut = rig.Build();

            var effects = new[]
            {
                new DecisionEffect(DecisionEffectKind.ScheduleDeadline, deadline: Deadline("a", At.AddMinutes(1))),
                new DecisionEffect(DecisionEffectKind.EmitEventTimelineEntry),
                new DecisionEffect(DecisionEffectKind.RunClassifier, classifierId: Id, classifierSnapshot: new HashSnapshot("h")),
                new DecisionEffect(DecisionEffectKind.PersistSnapshot),
                new DecisionEffect(DecisionEffectKind.CancelDeadline, cancelDeadlineName: "other"),
            };

            var result = await sut.RunAsync(effects, InitialState(), At);

            Assert.True(result.Success);
            Assert.Single(rig.Scheduler.Scheduled);
            Assert.Equal(1, rig.Emitter.CallCount);
            Assert.Equal(1, result.ClassifierInvocations);
            Assert.Single(rig.Snapshot.Saved);
            Assert.Single(rig.Scheduler.Cancelled);
        }

        [Fact]
        public async Task Empty_effects_list_returns_empty_success()
        {
            var rig = new Rig();
            var sut = rig.Build();

            var result = await sut.RunAsync(Array.Empty<DecisionEffect>(), InitialState(), At);

            Assert.True(result.Success);
            Assert.Equal(0, result.ClassifierInvocations);
        }
    }

    // Local exception type so we don't take a dep on System.IO.
    internal sealed class IOException_ : Exception
    {
        public IOException_(string msg) : base(msg) { }
    }

    // Tiny builder-extension helper so tests can set builder props inline.
    internal static class DecisionStateBuilderTestExt
    {
        public static DecisionStateBuilder Apply(this DecisionStateBuilder b, Action<DecisionStateBuilder> mutate)
        {
            mutate(b);
            return b;
        }
    }
}
