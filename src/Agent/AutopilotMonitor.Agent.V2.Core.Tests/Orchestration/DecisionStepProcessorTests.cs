using System;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    public sealed class DecisionStepProcessorTests
    {
        private static DateTime At => new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// Rig wires the processor against in-memory fakes + a real <see cref="DecisionEngine"/>
        /// so we get genuine <see cref="DecisionStep"/> instances (ctor-validated transition +
        /// effects) without duplicating the reducer shape in test builders.
        /// </summary>
        private sealed class Rig : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public AgentLogger Logger { get; }
            public FakeJournalWriter Journal { get; } = new FakeJournalWriter();
            public FakeEffectRunner Effects { get; } = new FakeEffectRunner();
            public FakeSnapshotPersistence Snapshot { get; } = new FakeSnapshotPersistence();
            public FakeQuarantineSink Quarantine { get; } = new FakeQuarantineSink();
            public DecisionEngine Engine { get; } = new DecisionEngine();
            public DecisionState InitialState { get; } = DecisionState.CreateInitial("S1", "T1");

            public Rig()
            {
                Logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
            }

            public DecisionStepProcessor Build(int threshold = DecisionStepProcessor.DefaultQuarantineThreshold) =>
                new DecisionStepProcessor(
                    initialState: InitialState,
                    journal: Journal,
                    effectRunner: Effects,
                    snapshot: Snapshot,
                    quarantineSink: Quarantine,
                    logger: Logger,
                    quarantineThreshold: threshold);

            /// <summary>Builds a real DecisionStep by Reducing SessionStarted against the current state.</summary>
            public (DecisionStep step, DecisionSignal signal) ReduceSessionStarted(long ordinal = 0)
            {
                var signal = TestSignals.Raw(ordinal, DecisionSignalKind.SessionStarted, At);
                var step = Engine.Reduce(InitialState, signal);
                return (step, signal);
            }

            public void Dispose() => Tmp.Dispose();
        }

        // ========================================================================= Ctor

        [Fact]
        public void Ctor_rejects_null_dependencies()
        {
            using var rig = new Rig();
            Assert.Throws<ArgumentNullException>(() =>
                new DecisionStepProcessor(null!, rig.Journal, rig.Effects, rig.Snapshot, rig.Quarantine, rig.Logger));
            Assert.Throws<ArgumentNullException>(() =>
                new DecisionStepProcessor(rig.InitialState, null!, rig.Effects, rig.Snapshot, rig.Quarantine, rig.Logger));
            Assert.Throws<ArgumentNullException>(() =>
                new DecisionStepProcessor(rig.InitialState, rig.Journal, null!, rig.Snapshot, rig.Quarantine, rig.Logger));
            Assert.Throws<ArgumentNullException>(() =>
                new DecisionStepProcessor(rig.InitialState, rig.Journal, rig.Effects, null!, rig.Quarantine, rig.Logger));
            Assert.Throws<ArgumentNullException>(() =>
                new DecisionStepProcessor(rig.InitialState, rig.Journal, rig.Effects, rig.Snapshot, null!, rig.Logger));
            Assert.Throws<ArgumentNullException>(() =>
                new DecisionStepProcessor(rig.InitialState, rig.Journal, rig.Effects, rig.Snapshot, rig.Quarantine, null!));
        }

        [Fact]
        public void Ctor_rejects_non_positive_threshold()
        {
            using var rig = new Rig();
            Assert.Throws<ArgumentOutOfRangeException>(() => rig.Build(threshold: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => rig.Build(threshold: -1));
        }

        [Fact]
        public void CurrentState_starts_at_initial_state()
        {
            using var rig = new Rig();
            var sut = rig.Build();
            Assert.Same(rig.InitialState, sut.CurrentState);
        }

        // ========================================================================= Happy path

        [Fact]
        public void ApplyStep_happy_path_appends_journal_runs_effects_saves_snapshot_forwards_state()
        {
            using var rig = new Rig();
            var sut = rig.Build();
            var (step, signal) = rig.ReduceSessionStarted();

            sut.ApplyStep(step, signal);

            Assert.Single(rig.Journal.Appended);
            Assert.Same(step.Transition, rig.Journal.Appended[0]);
            Assert.Equal(1, rig.Effects.CallCount);
            Assert.Same(step.NewState, rig.Effects.Calls[0].State);
            Assert.Equal(signal.OccurredAtUtc, rig.Effects.Calls[0].OccurredAtUtc);
            Assert.Single(rig.Snapshot.Saved);
            Assert.Same(step.NewState, rig.Snapshot.Saved[0]);
            Assert.Same(step.NewState, sut.CurrentState);
            Assert.Equal(0, sut.ConsecutiveJournalFailureCount);
            Assert.Empty(rig.Quarantine.Reasons);
        }

        // ========================================================================= Journal failures

        [Fact]
        public void ApplyStep_journal_fails_once_does_not_trigger_quarantine()
        {
            using var rig = new Rig();
            rig.Journal.ScriptThrow(new InvalidOperationException("disk-hiccup"));
            var sut = rig.Build();
            var (step, signal) = rig.ReduceSessionStarted();

            Assert.Throws<InvalidOperationException>(() => sut.ApplyStep(step, signal));

            Assert.Equal(1, sut.ConsecutiveJournalFailureCount);
            Assert.Empty(rig.Quarantine.Reasons);
            Assert.Equal(0, rig.Effects.CallCount);   // no effects run after append fails
            Assert.Empty(rig.Snapshot.Saved);
            Assert.Same(rig.InitialState, sut.CurrentState);   // state NOT advanced
        }

        [Fact]
        public void ApplyStep_journal_fails_threshold_times_triggers_quarantine_once()
        {
            using var rig = new Rig();
            rig.Journal.ScriptThrow(new InvalidOperationException("disk-full"), count: 5);
            var sut = rig.Build(threshold: 3);
            var (step, signal) = rig.ReduceSessionStarted();

            // Failures 1 + 2 must not quarantine.
            Assert.Throws<InvalidOperationException>(() => sut.ApplyStep(step, signal));
            Assert.Throws<InvalidOperationException>(() => sut.ApplyStep(step, signal));
            Assert.Empty(rig.Quarantine.Reasons);

            // Failure 3 crosses the threshold.
            Assert.Throws<InvalidOperationException>(() => sut.ApplyStep(step, signal));
            Assert.Single(rig.Quarantine.Reasons);
            Assert.Contains("journal append failed", rig.Quarantine.Reasons[0]);
            Assert.Contains("3x", rig.Quarantine.Reasons[0]);

            // Failure 4 must not re-trigger quarantine (fire-once).
            Assert.Throws<InvalidOperationException>(() => sut.ApplyStep(step, signal));
            Assert.Single(rig.Quarantine.Reasons);

            Assert.Same(rig.InitialState, sut.CurrentState);
        }

        [Fact]
        public void ApplyStep_journal_recovers_after_transient_failure_resets_counter()
        {
            using var rig = new Rig();
            rig.Journal.ScriptThrow(new InvalidOperationException("blip")).ScriptOk();
            var sut = rig.Build();
            var (step, signal) = rig.ReduceSessionStarted();

            Assert.Throws<InvalidOperationException>(() => sut.ApplyStep(step, signal));
            Assert.Equal(1, sut.ConsecutiveJournalFailureCount);

            sut.ApplyStep(step, signal);   // succeeds second time
            Assert.Equal(0, sut.ConsecutiveJournalFailureCount);
            Assert.Same(step.NewState, sut.CurrentState);
        }

        // ========================================================================= Snapshot best-effort

        [Fact]
        public void ApplyStep_snapshot_fails_does_not_fail_step()
        {
            using var rig = new Rig();
            rig.Snapshot.ScriptThrow(new InvalidOperationException("snapshot-disk-full"));
            var sut = rig.Build();
            var (step, signal) = rig.ReduceSessionStarted();

            sut.ApplyStep(step, signal);   // does NOT throw

            Assert.Single(rig.Journal.Appended);
            Assert.Equal(1, rig.Effects.CallCount);
            Assert.Same(step.NewState, sut.CurrentState);   // state still advanced
            Assert.Empty(rig.Quarantine.Reasons);
        }

        // ========================================================================= EffectRunner behavior

        [Fact]
        public void ApplyStep_effect_runner_SessionMustAbort_is_logged_but_step_committed()
        {
            using var rig = new Rig();
            var abortResult = new EffectRunResult(
                sessionMustAbort: true,
                abortReason: "timer_infrastructure_failure: timer-broken",
                failures: Array.Empty<AutopilotMonitor.Agent.V2.Core.Orchestration.EffectFailure>(),
                classifierInvocations: 0,
                classifierSkippedByAntiLoop: 0);
            rig.Effects.ScriptResult(abortResult);

            var sut = rig.Build();
            var (step, signal) = rig.ReduceSessionStarted();

            sut.ApplyStep(step, signal);   // state still advances

            Assert.Single(rig.Journal.Appended);
            Assert.Same(step.NewState, sut.CurrentState);
            Assert.Empty(rig.Quarantine.Reasons);   // effect-failures do NOT quarantine
        }

        [Fact]
        public void ApplyStep_effect_runner_unexpected_throw_logged_but_step_committed()
        {
            using var rig = new Rig();
            rig.Effects.ScriptThrow(new InvalidOperationException("unexpected"));
            var sut = rig.Build();
            var (step, signal) = rig.ReduceSessionStarted();

            sut.ApplyStep(step, signal);   // swallowed

            Assert.Single(rig.Journal.Appended);
            Assert.Same(step.NewState, sut.CurrentState);
        }

        // ========================================================================= Argument validation

        [Fact]
        public void ApplyStep_rejects_null_step_or_signal()
        {
            using var rig = new Rig();
            var sut = rig.Build();
            var (step, signal) = rig.ReduceSessionStarted();

            Assert.Throws<ArgumentNullException>(() => sut.ApplyStep(null!, signal));
            Assert.Throws<ArgumentNullException>(() => sut.ApplyStep(step, null!));
        }
    }
}
