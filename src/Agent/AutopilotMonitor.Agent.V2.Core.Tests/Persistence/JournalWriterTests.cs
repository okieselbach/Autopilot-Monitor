using System;
using System.IO;
using System.Text;
using AutopilotMonitor.Agent.V2.Core.Persistence;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.State;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Persistence
{
    public sealed class JournalWriterTests
    {
        private static DecisionTransition Step(int index, long signalRef = 0, SessionStage from = SessionStage.SessionStarted, SessionStage to = SessionStage.AwaitingEspPhaseChange)
        {
            return new DecisionTransition(
                stepIndex: index,
                sessionTraceOrdinal: index,
                signalOrdinalRef: signalRef,
                occurredAtUtc: new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc).AddSeconds(index),
                trigger: "TestTrigger",
                fromStage: from,
                toStage: to,
                taken: true,
                deadEndReason: null,
                reducerVersion: "2.0.0.0");
        }

        [Fact]
        public void Append_then_ReadAll_roundtrips_all_fields_including_guards()
        {
            using var tmp = new TempDirectory();
            var path = tmp.File("journal.jsonl");
            var w = new JournalWriter(path);

            var step = new DecisionTransition(
                stepIndex: 0,
                sessionTraceOrdinal: 5,
                signalOrdinalRef: 3,
                occurredAtUtc: new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc),
                trigger: "EspPhaseChanged",
                fromStage: SessionStage.SessionStarted,
                toStage: SessionStage.AwaitingEspPhaseChange,
                taken: true,
                deadEndReason: null,
                reducerVersion: "2.0.0.0",
                guards: new[]
                {
                    new GuardReport("phase-is-accountsetup", passed: true, reason: null),
                    new GuardReport("enrollment-not-terminal", passed: true, reason: null),
                },
                emittedEventSequences: new long[] { 42, 43 });

            w.Append(step);

            var read = w.ReadAll();
            Assert.Single(read);
            Assert.Equal(0, read[0].StepIndex);
            Assert.Equal(5, read[0].SessionTraceOrdinal);
            Assert.Equal(3, read[0].SignalOrdinalRef);
            Assert.Equal(2, read[0].Guards.Count);
            Assert.True(read[0].Guards[0].Passed);
            Assert.Equal("phase-is-accountsetup", read[0].Guards[0].GuardId);
            Assert.Equal(2, read[0].EmittedEventSequences.Count);
            Assert.Equal(42, read[0].EmittedEventSequences[0]);
        }

        [Fact]
        public void Append_stores_DeadEnd_with_reason()
        {
            using var tmp = new TempDirectory();
            var w = new JournalWriter(tmp.File("journal.jsonl"));

            var deadEnd = new DecisionTransition(
                stepIndex: 0,
                sessionTraceOrdinal: 7,
                signalOrdinalRef: 4,
                occurredAtUtc: new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc),
                trigger: "HelloResolved",
                fromStage: SessionStage.AwaitingEspPhaseChange,
                toStage: SessionStage.AwaitingEspPhaseChange,
                taken: false,
                deadEndReason: "hybrid_reboot_gate_blocking",
                reducerVersion: "2.0.0.0",
                guards: new[] { new GuardReport("hybrid-gate", passed: false, reason: "reboot pending") });

            w.Append(deadEnd);

            var back = w.ReadAll();
            Assert.False(back[0].Taken);
            Assert.Equal("hybrid_reboot_gate_blocking", back[0].DeadEndReason);
            Assert.False(back[0].Guards[0].Passed);
        }

        [Fact]
        public void Append_rejects_non_monotonic_stepIndex()
        {
            using var tmp = new TempDirectory();
            var w = new JournalWriter(tmp.File("journal.jsonl"));

            w.Append(Step(5));

            Assert.Throws<InvalidOperationException>(() => w.Append(Step(5)));
            Assert.Throws<InvalidOperationException>(() => w.Append(Step(3)));
        }

        [Fact]
        public void New_writer_on_existing_journal_recovers_LastStepIndex()
        {
            using var tmp = new TempDirectory();
            var path = tmp.File("journal.jsonl");

            var w1 = new JournalWriter(path);
            w1.Append(Step(0));
            w1.Append(Step(1));

            var w2 = new JournalWriter(path);
            Assert.Equal(1, w2.LastStepIndex);
            w2.Append(Step(2));
            Assert.Equal(3, w2.ReadAll().Count);
        }

        [Fact]
        public void ReadAll_stops_at_corrupt_tail_line()
        {
            using var tmp = new TempDirectory();
            var path = tmp.File("journal.jsonl");
            var w = new JournalWriter(path);

            w.Append(Step(0));
            w.Append(Step(1));
            File.AppendAllText(path, "{\"StepIndex\":2,\"Trigger\":\"Esp", Encoding.UTF8);

            var reread = new JournalWriter(path);
            Assert.Equal(1, reread.LastStepIndex);
            Assert.Equal(2, reread.ReadAll().Count);
        }
    }
}
