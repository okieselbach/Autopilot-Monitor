using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Persistence;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using Xunit;

// xUnit1031: Task.Wait / ManualResetEventSlim.Wait are used here intentionally to drive
// deterministic multi-thread scenarios (producer is blocked inside BlockingCollection.Add by
// design; switching to async/await would not match the semantics under test).
#pragma warning disable xUnit1031

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    [Collection("SerialThreading")]
    public sealed class SignalIngressTests
    {
        private static DateTime At => new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        private static Evidence RawEvidence(string id = "raw-1") =>
            new Evidence(EvidenceKind.Raw, id, $"evidence-{id}");

        /// <summary>
        /// Konstruktionshelfer — baut eine SignalIngress gegen In-Memory-Writer + Fake-Engine.
        /// </summary>
        private sealed class Rig : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public SignalLogWriter SignalLog { get; }
            public SessionTraceOrdinalProvider TraceCounter { get; } = new SessionTraceOrdinalProvider();
            public FakeDecisionStepProcessor Processor { get; } = new FakeDecisionStepProcessor();
            public FakeBackPressureObserver Observer { get; } = new FakeBackPressureObserver();
            public VirtualClock Clock { get; } = new VirtualClock(new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc));
            public DecisionEngine Engine { get; } = new DecisionEngine();

            public Rig()
            {
                SignalLog = new SignalLogWriter(Tmp.File("signal-log.jsonl"));
            }

            public SignalIngress Build(int channelCapacity = 256, bool wireObserver = true, TimeSpan? throttle = null) =>
                new SignalIngress(
                    engine: Engine,
                    signalLog: SignalLog,
                    traceCounter: TraceCounter,
                    processor: Processor,
                    clock: Clock,
                    backPressureObserver: wireObserver ? Observer : null,
                    channelCapacity: channelCapacity,
                    backPressureThrottle: throttle);

            public void Dispose() => Tmp.Dispose();
        }

        // 5000ms cushion: 2000ms was racy under ThreadPool contention from parallel test classes
        // even with [Collection("SerialThreading")] (timers on the same pool can still lag).
        // Plan §4.x M4.5.c.
        private static bool WaitFor(Func<bool> condition, int timeoutMs = 5000)
        {
            return SpinWait.SpinUntil(condition, timeoutMs);
        }

        // ============================================================ Lifecycle

        [Fact]
        public void Start_twice_throws()
        {
            using var rig = new Rig();
            var ing = rig.Build();
            ing.Start();
            Assert.Throws<InvalidOperationException>(() => ing.Start());
            ing.Stop();
        }

        [Fact]
        public void Stop_is_idempotent()
        {
            using var rig = new Rig();
            var ing = rig.Build();
            ing.Start();
            ing.Stop();
            ing.Stop();   // does not throw
        }

        [Fact]
        public void Post_after_Stop_throws()
        {
            using var rig = new Rig();
            var ing = rig.Build();
            ing.Start();
            ing.Stop();

            Assert.Throws<InvalidOperationException>(() =>
                ing.Post(DecisionSignalKind.SessionStarted, At, "Collector", RawEvidence()));
        }

        [Fact]
        public void Dispose_stops_worker_and_releases_channel()
        {
            using var rig = new Rig();
            var ing = rig.Build();
            ing.Start();
            ing.Dispose();

            Assert.Throws<InvalidOperationException>(() =>
                ing.Post(DecisionSignalKind.SessionStarted, At, "Collector", RawEvidence()));
        }

        [Fact]
        public void Post_before_Start_queues_until_worker_runs()
        {
            using var rig = new Rig();
            var ing = rig.Build();
            // Post before Start — item sits in the channel.
            ing.Post(DecisionSignalKind.SessionStarted, At, "Collector", RawEvidence());
            Assert.Equal(0, rig.Processor.ApplyCallCount);

            ing.Start();
            Assert.True(WaitFor(() => rig.Processor.ApplyCallCount == 1));
            ing.Stop();
        }

        // ============================================================ Ordinals + SignalLog

        [Fact]
        public void Worker_assigns_monotonic_ordinals_from_zero_on_empty_log()
        {
            using var rig = new Rig();
            var ing = rig.Build();
            ing.Start();

            for (int i = 0; i < 5; i++)
            {
                ing.Post(DecisionSignalKind.SessionStarted, At, "Collector", RawEvidence($"id-{i}"));
            }

            ing.Stop();

            var persisted = rig.SignalLog.ReadAll();
            Assert.Equal(5, persisted.Count);
            for (int i = 0; i < 5; i++) Assert.Equal(i, persisted[i].SessionSignalOrdinal);
            Assert.Equal(4, rig.SignalLog.LastOrdinal);
            Assert.Equal(4, ing.LastAssignedSignalOrdinal);
        }

        [Fact]
        public void Worker_seeds_ordinal_from_existing_SignalLog_on_recovery()
        {
            using var tmp = new TempDirectory();
            var logPath = tmp.File("signal-log.jsonl");

            // Pre-populate log with 3 signals, ordinals 0..2.
            var pre = new SignalLogWriter(logPath);
            for (int i = 0; i < 3; i++)
            {
                pre.Append(new DecisionSignal(
                    sessionSignalOrdinal: i,
                    sessionTraceOrdinal: i,
                    kind: DecisionSignalKind.SessionStarted,
                    kindSchemaVersion: 1,
                    occurredAtUtc: At,
                    sourceOrigin: "Seed",
                    evidence: RawEvidence($"pre-{i}")));
            }

            var recovered = new SignalLogWriter(logPath);
            var ing = new SignalIngress(
                engine: new DecisionEngine(),
                signalLog: recovered,
                traceCounter: new SessionTraceOrdinalProvider(2),
                processor: new FakeDecisionStepProcessor(),
                clock: new VirtualClock(At));
            ing.Start();

            ing.Post(DecisionSignalKind.DesktopArrived, At, "Collector", RawEvidence("fresh"));
            ing.Stop();

            Assert.Equal(3, ing.LastAssignedSignalOrdinal);
            var all = recovered.ReadAll();
            Assert.Equal(4, all.Count);
            Assert.Equal(3, all[3].SessionSignalOrdinal);
            Assert.Equal(DecisionSignalKind.DesktopArrived, all[3].Kind);
        }

        [Fact]
        public void SignalLog_append_happens_before_processor_ApplyStep()
        {
            // §2.7c / L.1: signal must be on disk before the reducer sees it.
            using var rig = new Rig();
            DecisionSignal? observedAtApply = null;
            long logOrdinalAtApply = -1;

            rig.Processor.ScriptOk(1);   // dummy to exercise the queue
            var processorSpy = new FakeDecisionStepProcessor();

            var ing = new SignalIngress(
                engine: rig.Engine,
                signalLog: rig.SignalLog,
                traceCounter: rig.TraceCounter,
                processor: new ApplyInspectingProcessor(rig.SignalLog, (step, signal, logAtApply) =>
                {
                    observedAtApply = signal;
                    logOrdinalAtApply = logAtApply;
                }),
                clock: rig.Clock,
                backPressureObserver: rig.Observer);
            ing.Start();

            ing.Post(DecisionSignalKind.SessionStarted, At, "Collector", RawEvidence());
            ing.Stop();

            Assert.NotNull(observedAtApply);
            Assert.Equal(0, observedAtApply!.SessionSignalOrdinal);
            Assert.Equal(0, logOrdinalAtApply);   // bereits appendet beim ApplyStep-Call
        }

        private sealed class ApplyInspectingProcessor : IDecisionStepProcessor
        {
            private readonly ISignalLogWriter _log;
            private readonly Action<DecisionStep, DecisionSignal, long> _inspect;
            private DecisionCore.State.DecisionState _state = DecisionCore.State.DecisionState.CreateInitial("S1", "T1");

            public ApplyInspectingProcessor(ISignalLogWriter log, Action<DecisionStep, DecisionSignal, long> inspect)
            {
                _log = log;
                _inspect = inspect;
            }

            public DecisionCore.State.DecisionState CurrentState => _state;

            public void ApplyStep(DecisionStep step, DecisionSignal signal)
            {
                _inspect(step, signal, _log.LastOrdinal);
                _state = step.NewState;
            }
        }

        // ============================================================ Reducer dispatch

        [Fact]
        public void ApplyStep_receives_state_at_step_begin_and_advances_for_next_signal()
        {
            // Reducer dispatches SessionStarted → state with Stage=EnrollmentStarted (or similar).
            // Second signal should see updated state from first.
            using var rig = new Rig();
            var ing = rig.Build();
            ing.Start();

            ing.Post(DecisionSignalKind.SessionStarted, At, "Collector", RawEvidence("s0"));
            ing.Post(DecisionSignalKind.DesktopArrived, At.AddSeconds(1), "Collector", RawEvidence("s1"));
            ing.Stop();

            Assert.Equal(2, rig.Processor.ApplyCallCount);
            var calls = rig.Processor.Calls;
            Assert.Equal(0, calls[0].Signal.SessionSignalOrdinal);
            Assert.Equal(1, calls[1].Signal.SessionSignalOrdinal);

            // StepIndex monoton.
            Assert.True(calls[1].Step.NewState.StepIndex > calls[0].Step.NewState.StepIndex);
        }

        [Fact]
        public void ApplyStep_exception_does_not_kill_worker_subsequent_signals_still_processed()
        {
            using var rig = new Rig();
            rig.Processor.ScriptThrow(new InvalidOperationException("journal-disk-full"));
            var ing = rig.Build();
            ing.Start();

            ing.Post(DecisionSignalKind.SessionStarted, At, "Collector", RawEvidence("s0"));
            // First ApplyStep throws — worker must survive.
            Assert.True(WaitFor(() => rig.Processor.ApplyCallCount == 1));

            ing.Post(DecisionSignalKind.DesktopArrived, At.AddSeconds(1), "Collector", RawEvidence("s1"));
            Assert.True(WaitFor(() => rig.Processor.ApplyCallCount == 2));
            ing.Stop();

            Assert.Equal(2, rig.SignalLog.ReadAll().Count);
        }

        // ============================================================ Codex Finding 4 — SignalPosted

        [Fact]
        public void SignalPosted_fires_for_every_successful_post_regardless_of_kind()
        {
            using var rig = new Rig();
            var ing = rig.Build();
            ing.Start();

            var observed = new List<(DecisionSignalKind Kind, string? EventType)>();
            ing.SignalPosted += (kind, payload) =>
            {
                string? eventType = null;
                payload?.TryGetValue("eventType", out eventType);
                lock (observed) observed.Add((kind, eventType));
            };

            // Cross-source simulation: different InformationalEventPost instances all hitting
            // the same ingress. Before Finding 4's fix, only one instance's posts would reach
            // the observer; now all of them do because the event lives on the ingress itself.
            ISignalIngressSink sink = ing;
            var postA = new InformationalEventPost(sink, rig.Clock);
            var postB = new InformationalEventPost(sink, rig.Clock);

            postA.Emit(eventType: "esp_phase_changed", source: "EspAndHelloTracker");
            postB.Emit(eventType: "ime_user_session_completed", source: "ImeLogTracker");
            sink.Post(
                DecisionSignalKind.ClassifierVerdictIssued,
                At,
                "effectrunner:classifier:whiteglove_sealing",
                new Evidence(EvidenceKind.Synthetic, "classifier:abc", "Strong/85"),
                new Dictionary<string, string> { ["level"] = "Strong" });

            ing.Stop();

            Assert.Equal(3, observed.Count);
            Assert.Contains(observed, o => o.Kind == DecisionSignalKind.InformationalEvent && o.EventType == "esp_phase_changed");
            Assert.Contains(observed, o => o.Kind == DecisionSignalKind.InformationalEvent && o.EventType == "ime_user_session_completed");
            Assert.Contains(observed, o => o.Kind == DecisionSignalKind.ClassifierVerdictIssued);
        }

        [Fact]
        public void SignalPosted_handler_exception_does_not_break_ingress()
        {
            using var rig = new Rig();
            var ing = rig.Build();
            ing.Start();

            ing.SignalPosted += (_, _) => throw new InvalidOperationException("observer bug");

            // The post MUST succeed and the signal MUST still reach the reducer — observer
            // exceptions are advisory-only.
            ing.Post(
                DecisionSignalKind.SessionStarted,
                At,
                "test",
                new Evidence(EvidenceKind.Synthetic, "session-start", "first signal"));

            ing.Stop();

            Assert.Single(rig.Processor.Calls);
        }

        [Fact]
        public void SignalPosted_is_raised_even_when_channel_took_slow_path()
        {
            // The back-pressured slow path (BlockingCollection.Add after TryAdd fails) must
            // still trigger the observer so the idle clock is reset under load.
            using var rig = new Rig();
            var ing = rig.Build(channelCapacity: 1);
            ing.Start();

            var observedCount = 0;
            ing.SignalPosted += (_, _) => Interlocked.Increment(ref observedCount);

            for (int i = 0; i < 5; i++)
            {
                ing.Post(
                    DecisionSignalKind.SessionStarted,
                    At,
                    "test",
                    new Evidence(EvidenceKind.Synthetic, $"signal-{i}", "slow-path probe"));
            }

            ing.Stop();

            Assert.Equal(5, observedCount);
        }

        [Fact]
        public void Post_via_ISignalIngressSink_reaches_reducer_like_any_signal()
        {
            using var rig = new Rig();
            var ing = rig.Build();
            ISignalIngressSink sink = ing;
            ing.Start();

            sink.Post(
                DecisionSignalKind.ClassifierVerdictIssued,
                At,
                "effectrunner:classifier:whiteglove_sealing",
                new Evidence(EvidenceKind.Synthetic, "classifier:whiteglove_sealing:abc", "Strong/85"),
                new Dictionary<string, string> { ["level"] = "Strong" });

            ing.Stop();

            Assert.Single(rig.Processor.Calls);
            Assert.Equal(DecisionSignalKind.ClassifierVerdictIssued, rig.Processor.Calls[0].Signal.Kind);
            Assert.Equal("Strong", rig.Processor.Calls[0].Signal.Payload!["level"]);
        }

        [Fact]
        public void Post_null_evidence_throws_synchronously_without_enqueuing()
        {
            using var rig = new Rig();
            var ing = rig.Build();
            ing.Start();

            Assert.Throws<ArgumentNullException>(() =>
                ing.Post(DecisionSignalKind.SessionStarted, At, "Collector", null!));

            ing.Stop();
            Assert.Empty(rig.SignalLog.ReadAll());
        }

        [Fact]
        public void Post_empty_origin_throws_synchronously()
        {
            using var rig = new Rig();
            var ing = rig.Build();
            ing.Start();

            Assert.Throws<ArgumentException>(() =>
                ing.Post(DecisionSignalKind.SessionStarted, At, "", RawEvidence()));

            ing.Stop();
        }

        // ============================================================ Back-pressure

        [Fact]
        public void Fast_path_does_not_notify_back_pressure()
        {
            using var rig = new Rig();
            var ing = rig.Build(channelCapacity: 32);
            ing.Start();

            for (int i = 0; i < 20; i++)
            {
                ing.Post(DecisionSignalKind.SessionStarted, At, "Collector", RawEvidence($"id-{i}"));
            }
            ing.Stop();

            Assert.Equal(20, rig.Processor.ApplyCallCount);
            Assert.Equal(0, rig.Observer.CallCount);
        }

        /// <summary>
        /// Posts <paramref name="signalCount"/>-1 signals to fill the channel, then starts
        /// a producer task that will block on Add. Returns once the producer is highly
        /// likely blocked inside Add (task has started + signaled readiness + small sleep).
        /// </summary>
        private static Task SpawnBlockedProducer(SignalIngress ing, string origin, string evidenceId)
        {
            var producerRunning = new ManualResetEventSlim(false);
            var task = Task.Run(() =>
            {
                producerRunning.Set();   // task has been scheduled and is executing
                ing.Post(DecisionSignalKind.DesktopArrived, At, origin, new Evidence(EvidenceKind.Raw, evidenceId, $"ev-{evidenceId}"));
            });
            Assert.True(producerRunning.Wait(2000), "Producer task did not start within 2s");
            // Small grace period: giving the producer time to reach the blocking Add call.
            // After Set() the lambda still has to step into Post → TryAdd fails → Add blocks.
            Thread.Sleep(100);
            return task;
        }

        [Fact]
        public void Full_channel_triggers_back_pressure_observer_once()
        {
            using var rig = new Rig();
            var ing = rig.Build(channelCapacity: 1);

            // Fill channel without running worker.
            ing.Post(DecisionSignalKind.SessionStarted, At, "Collector", RawEvidence("seed"));
            Assert.Equal(1, ing.ApproximateQueueLength);

            // Producer that will block on Add().
            var producer = SpawnBlockedProducer(ing, "Collector", "blocked");

            // Let worker drain; producer's Add returns after one slot frees.
            ing.Start();
            Assert.True(producer.Wait(5000));
            ing.Stop();

            Assert.Equal(1, rig.Observer.CallCount);
            var call = rig.Observer.Calls[0];
            Assert.Equal("Collector", call.Origin);
            Assert.Equal(1, call.ChannelCapacity);
            Assert.True(call.BlockDuration >= TimeSpan.Zero);
        }

        [Fact]
        public void Back_pressure_is_throttled_within_window_per_origin()
        {
            using var rig = new Rig();
            var ing = rig.Build(channelCapacity: 1, throttle: TimeSpan.FromMinutes(1));

            // --- Round 1: trigger one back-pressure event from "A".
            ing.Post(DecisionSignalKind.SessionStarted, At, "A", RawEvidence("a1"));
            var t1 = SpawnBlockedProducer(ing, "A", "a2");
            ing.Start();
            Assert.True(t1.Wait(5000));
            Assert.True(WaitFor(() => rig.Processor.ApplyCallCount == 2));

            Assert.Equal(1, rig.Observer.CallCount);

            // --- Round 2 (same origin, no clock advance): observer should NOT fire (throttled).
            var blocker = new ManualResetEventSlim(false);
            rig.Processor.BlockHandle = blocker;
            ing.Post(DecisionSignalKind.SessionStarted, At, "A", RawEvidence("a3"));
            // Wait until worker has pulled it and is blocked on the BlockHandle; channel empty.
            Assert.True(WaitFor(() => ing.ApproximateQueueLength == 0));
            // Fill channel then spawn blocked producer.
            ing.Post(DecisionSignalKind.SessionStarted, At, "A", RawEvidence("a4"));
            Assert.True(WaitFor(() => ing.ApproximateQueueLength == 1));
            var t2 = SpawnBlockedProducer(ing, "A", "a5");
            blocker.Set();
            Assert.True(t2.Wait(5000));
            Assert.True(WaitFor(() => rig.Processor.ApplyCallCount == 5));

            Assert.Equal(1, rig.Observer.CallCount);   // STILL 1 — throttle active.
            ing.Stop();
        }

        [Fact]
        public void Different_origins_do_not_share_back_pressure_throttle()
        {
            using var rig = new Rig();
            var ing = rig.Build(channelCapacity: 1);

            // Origin "A" triggers BP.
            ing.Post(DecisionSignalKind.SessionStarted, At, "A", RawEvidence("a1"));
            var t1 = SpawnBlockedProducer(ing, "A", "a2");
            ing.Start();
            Assert.True(t1.Wait(5000));
            Assert.True(WaitFor(() => rig.Processor.ApplyCallCount == 2));

            // Origin "B" triggers BP in the same throttle window.
            var blocker = new ManualResetEventSlim(false);
            rig.Processor.BlockHandle = blocker;
            ing.Post(DecisionSignalKind.SessionStarted, At, "B", RawEvidence("b1"));
            Assert.True(WaitFor(() => ing.ApproximateQueueLength == 0));
            ing.Post(DecisionSignalKind.SessionStarted, At, "B", RawEvidence("b2"));
            Assert.True(WaitFor(() => ing.ApproximateQueueLength == 1));
            var t2 = SpawnBlockedProducer(ing, "B", "b3");
            blocker.Set();
            Assert.True(t2.Wait(5000));

            ing.Stop();

            Assert.Equal(2, rig.Observer.CallCount);
            Assert.Contains(rig.Observer.Calls, c => c.Origin == "A");
            Assert.Contains(rig.Observer.Calls, c => c.Origin == "B");
        }

        [Fact]
        public void Clock_advance_past_throttle_window_re_arms_emission()
        {
            using var rig = new Rig();
            var ing = rig.Build(channelCapacity: 1, throttle: TimeSpan.FromMinutes(1));

            // Round 1 — BP for "A".
            ing.Post(DecisionSignalKind.SessionStarted, At, "A", RawEvidence("a1"));
            var t1 = SpawnBlockedProducer(ing, "A", "a2");
            ing.Start();
            Assert.True(t1.Wait(5000));
            Assert.True(WaitFor(() => rig.Processor.ApplyCallCount == 2));
            Assert.Equal(1, rig.Observer.CallCount);

            // Advance past throttle window.
            rig.Clock.Advance(TimeSpan.FromMinutes(2));

            // Round 2 — same origin, should fire again.
            var blocker = new ManualResetEventSlim(false);
            rig.Processor.BlockHandle = blocker;
            ing.Post(DecisionSignalKind.SessionStarted, At, "A", RawEvidence("a3"));
            Assert.True(WaitFor(() => ing.ApproximateQueueLength == 0));
            ing.Post(DecisionSignalKind.SessionStarted, At, "A", RawEvidence("a4"));
            Assert.True(WaitFor(() => ing.ApproximateQueueLength == 1));
            var t2 = SpawnBlockedProducer(ing, "A", "a5");
            blocker.Set();
            Assert.True(t2.Wait(5000));

            ing.Stop();
            Assert.Equal(2, rig.Observer.CallCount);
        }

        [Fact]
        public void Null_observer_is_supported_and_back_pressure_is_silent()
        {
            using var rig = new Rig();
            var ing = rig.Build(channelCapacity: 1, wireObserver: false);

            ing.Post(DecisionSignalKind.SessionStarted, At, "A", RawEvidence("a1"));
            var t1 = SpawnBlockedProducer(ing, "A", "a2");
            ing.Start();
            Assert.True(t1.Wait(5000));
            ing.Stop();

            // Observer is null — verifying no NRE crashed the producer or worker.
            Assert.Equal(2, rig.Processor.ApplyCallCount);
        }

        [Fact]
        public void Observer_exception_does_not_halt_producer_or_worker()
        {
            using var rig = new Rig();
            rig.Observer.ThrowOnNotify = new InvalidOperationException("observer bug");
            var ing = rig.Build(channelCapacity: 1);

            ing.Post(DecisionSignalKind.SessionStarted, At, "A", RawEvidence("a1"));
            var t1 = SpawnBlockedProducer(ing, "A", "a2");
            ing.Start();
            Assert.True(t1.Wait(5000));
            ing.Stop();

            Assert.Equal(2, rig.Processor.ApplyCallCount);
            Assert.Equal(1, rig.Observer.CallCount);   // recorded despite the throw
        }

        // ============================================================ Ordering + stress

        [Fact]
        public async Task Concurrent_producers_result_in_strictly_monotonic_ordinals()
        {
            using var rig = new Rig();
            var ing = rig.Build(channelCapacity: 64);
            ing.Start();

            const int Producers = 8;
            const int PerProducer = 50;

            var tasks = new Task[Producers];
            for (int p = 0; p < Producers; p++)
            {
                int idx = p;
                tasks[p] = Task.Run(() =>
                {
                    for (int i = 0; i < PerProducer; i++)
                    {
                        ing.Post(DecisionSignalKind.SessionStarted, At, $"p{idx}", RawEvidence($"p{idx}-{i}"));
                    }
                });
            }

            await Task.WhenAll(tasks);
            ing.Stop();

            var persisted = rig.SignalLog.ReadAll();
            Assert.Equal(Producers * PerProducer, persisted.Count);

            // Strictly increasing ordinals, contiguous from 0.
            for (int i = 0; i < persisted.Count; i++)
            {
                Assert.Equal(i, persisted[i].SessionSignalOrdinal);
            }
            // Trace ordinals must be strictly increasing (not necessarily = signal ordinal).
            var traces = persisted.Select(s => s.SessionTraceOrdinal).ToArray();
            for (int i = 1; i < traces.Length; i++)
            {
                Assert.True(traces[i] > traces[i - 1]);
            }
        }
    }
}
