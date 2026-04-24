using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Persistence;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Transport;
using AutopilotMonitor.Agent.V2.Core.Transport.Telemetry;
using AutopilotMonitor.DecisionCore.Classifiers;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using Xunit;

#pragma warning disable xUnit1031

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    public sealed class RecoveryPathTests
    {
        private static DateTime At => new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        private sealed class Rig : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public VirtualClock Clock { get; } = new VirtualClock(At);
            public AgentLogger Logger { get; }
            public FakeBackendTelemetryUploader Uploader { get; } = new FakeBackendTelemetryUploader();
            public string StateDir { get; }
            public string TransportDir { get; }

            public Rig()
            {
                Logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
                StateDir = Path.Combine(Tmp.Path, "State");
                TransportDir = Path.Combine(Tmp.Path, "Transport");
            }

            public EnrollmentOrchestrator Build() =>
                new EnrollmentOrchestrator(
                    sessionId: "S1",
                    tenantId: "T1",
                    stateDirectory: StateDir,
                    transportDirectory: TransportDir,
                    clock: Clock,
                    logger: Logger,
                    uploader: Uploader,
                    classifiers: new List<IClassifier>(),
                    drainInterval: TimeSpan.FromDays(1),
                    terminalDrainTimeout: TimeSpan.FromSeconds(2));

            public void Dispose() => Tmp.Dispose();
        }

        // ================================================================= Sonderfall 1: WG Part-1 Resume

        [Fact]
        public void Whiteglove_part1_snapshot_triggers_session_recovered_bridge_on_start()
        {
            using var rig = new Rig();
            Directory.CreateDirectory(rig.StateDir);

            // Seed a snapshot with stage = WhiteGloveSealed (Part 1 complete pre-reboot).
            var sealedState = DecisionState.CreateInitial("S1", "T1")
                .ToBuilder()
                .WithStage(SessionStage.WhiteGloveSealed)
                .WithStepIndex(5)
                .Build();
            new SnapshotPersistence(Path.Combine(rig.StateDir, "snapshot.json")).Save(sealedState);

            var sut = rig.Build();
            sut.Start();

            Assert.True(sut.IsWhiteGlovePart2Resume);
            Assert.False(sut.WasStartupQuarantine);

            // After ingress start, the orchestrator posts SessionRecovered — the reducer bridges
            // to WhiteGloveAwaitingUserSignIn. Wait for the signal-log to contain the signal.
            var signalLog = GetSignalLog(sut);
            Assert.True(SpinWait.SpinUntil(
                () => signalLog.ReadAll().Any(s => s.Kind == DecisionSignalKind.SessionRecovered),
                3000));

            // State must have advanced past WhiteGloveSealed to WhiteGloveAwaitingUserSignIn.
            Assert.True(SpinWait.SpinUntil(
                () => sut.CurrentState.Stage == SessionStage.WhiteGloveAwaitingUserSignIn,
                3000));

            sut.Stop();
        }

        [Fact]
        public void Non_whiteglove_snapshot_does_not_set_part2_resume_flag()
        {
            using var rig = new Rig();
            Directory.CreateDirectory(rig.StateDir);

            // Seed snapshot with a non-WG-sealed stage.
            var midState = DecisionState.CreateInitial("S1", "T1")
                .ToBuilder()
                .WithStage(SessionStage.EspDeviceSetup)
                .WithStepIndex(3)
                .Build();
            new SnapshotPersistence(Path.Combine(rig.StateDir, "snapshot.json")).Save(midState);

            var sut = rig.Build();
            sut.Start();

            Assert.False(sut.IsWhiteGlovePart2Resume);

            // No SessionRecovered should have been posted.
            Thread.Sleep(100);
            var signalLog = GetSignalLog(sut);
            Assert.DoesNotContain(signalLog.ReadAll(), s => s.Kind == DecisionSignalKind.SessionRecovered);

            sut.Stop();
        }

        // ================================================================= Sonderfall 2: Segment-Quarantine

        [Fact]
        public void Corrupt_snapshot_file_triggers_quarantine_and_fresh_start()
        {
            using var rig = new Rig();
            Directory.CreateDirectory(rig.StateDir);

            // Write a snapshot file that claims a valid checksum but has mismatching content.
            var snapshotPath = Path.Combine(rig.StateDir, "snapshot.json");
            File.WriteAllText(
                snapshotPath,
                "{\"Checksum\":\"deadbeef\",\"State\":{\"SessionId\":\"S1\",\"TenantId\":\"T1\"}}",
                Encoding.UTF8);

            // Seed a non-empty signal-log too so we can verify it also gets quarantined.
            File.WriteAllText(
                Path.Combine(rig.StateDir, "signal-log.jsonl"),
                "{\"fake\": \"stale signal\"}\n",
                Encoding.UTF8);

            var sut = rig.Build();
            sut.Start();

            Assert.True(sut.WasStartupQuarantine);
            Assert.False(sut.IsWhiteGlovePart2Resume);

            // Original snapshot + signal-log were moved aside.
            Assert.False(File.Exists(snapshotPath));
            var signalLogPath = Path.Combine(rig.StateDir, "signal-log.jsonl");
            if (File.Exists(signalLogPath))
            {
                // Fresh file should be empty (writer reinstantiated).
                Assert.True(new FileInfo(signalLogPath).Length == 0);
            }

            var quarantineRoot = Path.Combine(rig.StateDir, ".quarantine");
            Assert.True(Directory.Exists(quarantineRoot));
            var buckets = Directory.GetDirectories(quarantineRoot);
            Assert.NotEmpty(buckets);

            // Bucket contains reason + moved segment(s).
            var bucket = buckets[0];
            Assert.True(File.Exists(Path.Combine(bucket, "reason.txt")));

            // Fresh initial state.
            Assert.Equal(SessionStage.SessionStarted, sut.CurrentState.Stage);
            Assert.Equal(0, sut.CurrentState.StepIndex);

            sut.Stop();
        }

        [Fact]
        public void Missing_snapshot_does_not_trigger_quarantine()
        {
            using var rig = new Rig();
            var sut = rig.Build();

            sut.Start();

            Assert.False(sut.WasStartupQuarantine);
            Assert.Equal(SessionStage.SessionStarted, sut.CurrentState.Stage);

            sut.Stop();
        }

        // ============================================================ Codex #1 — ReducerReplay recovery paths

        private static DecisionSignal MakeSignal(
            long ordinal,
            DecisionSignalKind kind = DecisionSignalKind.AppInstallCompleted)
        {
            return new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: kind,
                kindSchemaVersion: 1,
                occurredAtUtc: At.AddSeconds(ordinal),
                sourceOrigin: "recovery-tests",
                evidence: new Evidence(
                    EvidenceKind.Synthetic,
                    $"recovery:ord-{ordinal}",
                    "test signal"));
        }

        private static DecisionTransition MakeTransition(int stepIndex, long signalRef)
        {
            return new DecisionTransition(
                stepIndex: stepIndex,
                sessionTraceOrdinal: stepIndex,
                signalOrdinalRef: signalRef,
                occurredAtUtc: At.AddSeconds(stepIndex),
                trigger: "TestTrigger",
                fromStage: SessionStage.SessionStarted,
                toStage: SessionStage.SessionStarted,
                taken: true,
                deadEndReason: null,
                reducerVersion: "2.0.0.0");
        }

        [Fact]
        public void Snapshot_plus_pending_tail_replays_tail_onto_snapshot()
        {
            // Crash-lag scenario: SignalLog has caught up to ordinal 1 but the Snapshot was only
            // flushed after the ordinal-0 reduce. On restart the orchestrator must replay the
            // tail (ordinal 1) on top of the snapshot to reach the real post-crash state.
            using var rig = new Rig();
            Directory.CreateDirectory(rig.StateDir);

            // Seed two signals on the log.
            var signalLogPath = Path.Combine(rig.StateDir, "signal-log.jsonl");
            var seedLog = new SignalLogWriter(signalLogPath);
            var sigs = new[]
            {
                MakeSignal(0, DecisionSignalKind.SessionStarted),
                MakeSignal(1, DecisionSignalKind.AppInstallCompleted),
            };
            foreach (var s in sigs) seedLog.Append(s);

            // Compute the expected final state by folding through the real reducer.
            var engine = new DecisionEngine();
            var expected = ReducerReplay.Replay(
                engine, DecisionState.CreateInitial("S1", "T1"), sigs);

            // Persist a snapshot that lags one step behind (only ordinal 0 consumed).
            var snapshotState = engine.Reduce(
                DecisionState.CreateInitial("S1", "T1"), sigs[0]).NewState;
            new SnapshotPersistence(
                Path.Combine(rig.StateDir, "snapshot.json"),
                () => rig.Clock.UtcNow).Save(snapshotState);

            var sut = rig.Build();
            sut.Start();

            Assert.False(sut.WasStartupQuarantine);
            Assert.Equal(expected.StepIndex, sut.CurrentState.StepIndex);
            Assert.Equal(expected.LastAppliedSignalOrdinal, sut.CurrentState.LastAppliedSignalOrdinal);
            Assert.Equal(expected.Stage, sut.CurrentState.Stage);

            sut.Stop();
        }

        [Fact]
        public void Corrupt_snapshot_with_intact_log_replays_full_log_without_quarantining_log()
        {
            // Snapshot corrupt (checksum fails) but the SignalLog is fine: the orchestrator
            // quarantines the snapshot only and rebuilds state from the full log.
            using var rig = new Rig();
            Directory.CreateDirectory(rig.StateDir);

            // Write a valid-looking snapshot file with a wrong checksum.
            File.WriteAllText(
                Path.Combine(rig.StateDir, "snapshot.json"),
                "{\"Checksum\":\"deadbeef\",\"State\":{\"SessionId\":\"S1\",\"TenantId\":\"T1\"}}",
                Encoding.UTF8);

            // Seed a parseable SignalLog with real signals.
            var sigs = new[]
            {
                MakeSignal(0, DecisionSignalKind.SessionStarted),
                MakeSignal(1, DecisionSignalKind.AppInstallCompleted),
                MakeSignal(2, DecisionSignalKind.AppInstallCompleted),
            };
            var seedLog = new SignalLogWriter(Path.Combine(rig.StateDir, "signal-log.jsonl"));
            foreach (var s in sigs) seedLog.Append(s);

            var engine = new DecisionEngine();
            var expected = ReducerReplay.Replay(
                engine, DecisionState.CreateInitial("S1", "T1"), sigs);

            var sut = rig.Build();
            sut.Start();

            // Snapshot quarantined, log survived, state reflects the full replay.
            Assert.True(sut.WasStartupQuarantine);
            Assert.Equal(expected.StepIndex, sut.CurrentState.StepIndex);
            Assert.Equal(expected.LastAppliedSignalOrdinal, sut.CurrentState.LastAppliedSignalOrdinal);

            var signalLogPath = Path.Combine(rig.StateDir, "signal-log.jsonl");
            Assert.True(File.Exists(signalLogPath));
            Assert.True(new FileInfo(signalLogPath).Length > 0); // log NOT quarantined

            sut.Stop();
        }

        [Fact]
        public void Journal_ahead_of_replayed_state_triggers_phantom_truncate()
        {
            // Pathological crash scenario: the Journal flushed step N but the Snapshot saved
            // only step N-1 and a later SignalLog append was never actually persisted (log
            // tail physically shorter than journal). On recovery the journal suffix is a
            // phantom — TruncateAfter moves it to .quarantine and realigns so the next live
            // append does not violate monotonicity.
            using var rig = new Rig();
            Directory.CreateDirectory(rig.StateDir);

            // Step 1: SignalLog has ordinals 0 + 1 only.
            var sigs = new[]
            {
                MakeSignal(0, DecisionSignalKind.SessionStarted),
                MakeSignal(1, DecisionSignalKind.AppInstallCompleted),
            };
            var seedLog = new SignalLogWriter(Path.Combine(rig.StateDir, "signal-log.jsonl"));
            foreach (var s in sigs) seedLog.Append(s);

            // Step 2: Snapshot lags behind — state after only the first reduce.
            var engine = new DecisionEngine();
            var snapshotState = engine.Reduce(
                DecisionState.CreateInitial("S1", "T1"), sigs[0]).NewState;
            new SnapshotPersistence(
                Path.Combine(rig.StateDir, "snapshot.json"),
                () => rig.Clock.UtcNow).Save(snapshotState);

            // Step 3: Journal has 3 transitions on disk (StepIndex 0, 1, 2) — the StepIndex-2
            // entry is the phantom (its signal was never flushed to the log).
            var journalPath = Path.Combine(rig.StateDir, "journal.jsonl");
            var seedJournal = new JournalWriter(journalPath, () => rig.Clock.UtcNow);
            seedJournal.Append(MakeTransition(0, signalRef: 0));
            seedJournal.Append(MakeTransition(1, signalRef: 1));
            seedJournal.Append(MakeTransition(2, signalRef: 99)); // phantom

            // Expected post-recovery state: replay tail [sig1] → StepIndex=2, LastApplied=1.
            var expected = ReducerReplay.Replay(engine, snapshotState, new[] { sigs[1] });
            Assert.Equal(2, expected.StepIndex);

            var sut = rig.Build();
            sut.Start();

            Assert.Equal(expected.StepIndex, sut.CurrentState.StepIndex);
            Assert.Equal(expected.LastAppliedSignalOrdinal, sut.CurrentState.LastAppliedSignalOrdinal);

            // Phantom file captured in the quarantine bucket.
            var quarantineRoot = Path.Combine(rig.StateDir, ".quarantine");
            Assert.True(Directory.Exists(quarantineRoot));
            var phantomFiles = Directory.GetFiles(
                quarantineRoot, "journal-phantom-tail.jsonl", SearchOption.AllDirectories);
            Assert.Single(phantomFiles);
            Assert.Single(File.ReadAllLines(phantomFiles[0]), l => !string.IsNullOrWhiteSpace(l));

            sut.Stop();
        }

        // ============================================================ Codex #1 Phase 3 — deadline re-arm

        [Fact]
        public void Snapshot_with_past_due_deadline_fires_DeadlineFired_signal_on_start()
        {
            // Past-due re-arm: a deadline whose DueAtUtc lies before clock.UtcNow must fire
            // immediately on rehydrate. The scheduler's past-due path queues to ThreadPool,
            // the Fired bridge synthesises a DeadlineFired signal, and it lands on the log.
            using var rig = new Rig();
            Directory.CreateDirectory(rig.StateDir);

            var pastDue = new ActiveDeadline(
                name: "hello_safety",
                dueAtUtc: At.AddMinutes(-5),
                firesSignalKind: DecisionSignalKind.DeadlineFired,
                firesPayload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["deadlineName"] = "hello_safety",
                });
            var seed = DecisionState.CreateInitial("S1", "T1")
                .ToBuilder()
                .WithStage(SessionStage.AwaitingHello)
                .WithStepIndex(3)
                .WithLastAppliedSignalOrdinal(2)
                .AddDeadline(pastDue)
                .Build();
            new SnapshotPersistence(
                Path.Combine(rig.StateDir, "snapshot.json"),
                () => rig.Clock.UtcNow).Save(seed);

            var sut = rig.Build();
            sut.Start();

            var signalLog = GetSignalLog(sut);
            Assert.True(
                SpinWait.SpinUntil(
                    () => signalLog.ReadAll().Any(s =>
                        s.Kind == DecisionSignalKind.DeadlineFired &&
                        s.Payload != null &&
                        s.Payload.TryGetValue("deadlineName", out var n) &&
                        n == "hello_safety"),
                    3000),
                "Expected DeadlineFired signal for re-armed past-due deadline.");

            sut.Stop();
        }

        [Fact]
        public void Snapshot_with_future_deadline_is_scheduled_but_not_yet_fired()
        {
            // Future re-arm: the rehydrated deadline is live on the scheduler (IsScheduled=true)
            // but no DeadlineFired signal has been emitted because its wall-clock due time
            // has not arrived.
            using var rig = new Rig();
            Directory.CreateDirectory(rig.StateDir);

            var future = new ActiveDeadline(
                name: "part2_safety",
                dueAtUtc: DateTime.UtcNow.AddHours(2), // real wall-clock future (scheduler uses wall clock)
                firesSignalKind: DecisionSignalKind.DeadlineFired);
            var seed = DecisionState.CreateInitial("S1", "T1")
                .ToBuilder()
                .WithStage(SessionStage.WhiteGloveSealed)
                .WithStepIndex(5)
                .WithLastAppliedSignalOrdinal(4)
                .AddDeadline(future)
                .Build();
            new SnapshotPersistence(
                Path.Combine(rig.StateDir, "snapshot.json"),
                () => rig.Clock.UtcNow).Save(seed);

            var sut = rig.Build();
            sut.Start();

            // Scheduler must know about the re-armed deadline.
            var scheduler = GetScheduler(sut);
            Assert.True(scheduler.IsScheduled("part2_safety"),
                "Future-due persisted deadline was not re-armed on the scheduler.");

            // Give the ThreadPool a moment to prove we don't fire it prematurely.
            Thread.Sleep(100);
            var signalLog = GetSignalLog(sut);
            Assert.DoesNotContain(signalLog.ReadAll(),
                s => s.Kind == DecisionSignalKind.DeadlineFired);

            sut.Stop();
        }

        // ================================================================= Sonderfall 3: Transport-Resume

        [Fact]
        public void Transport_resumes_from_persisted_cursor_after_restart()
        {
            using var rig = new Rig();
            Directory.CreateDirectory(rig.TransportDir);

            // Seed spool with 3 items (ItemIds 0/1/2, zero-based) and mark ItemId 0 as
            // already uploaded. Remaining pending: ItemIds 1 + 2.
            var spoolDir = rig.TransportDir;
            var seedSpool = new TelemetrySpool(spoolDir, rig.Clock);
            for (int i = 1; i <= 3; i++)
            {
                seedSpool.Enqueue(new TelemetryItemDraft(
                    kind: TelemetryItemKind.Event,
                    partitionKey: "T1_S1",
                    rowKey: $"row-{i}",
                    payloadJson: $"{{\"n\":{i}}}",
                    isSessionScoped: true,
                    requiresImmediateFlush: false));
            }
            seedSpool.MarkUploaded(0);   // pretend ItemId 0 already uploaded → 2 pending

            // Re-open via Orchestrator — uploader is scripted OK so the remaining 2 items drain.
            rig.Uploader.QueueOk(10);
            var sut = rig.Build();
            sut.Start();

            // Explicit drain — tests don't wait for the periodic loop.
            var drainResult = InvokeDrain(sut);
            Assert.True(drainResult.Success);
            Assert.Equal(2, drainResult.UploadedItems);

            // Uploader received one batch containing only items 2 + 3 (cursor was 1 on startup).
            Assert.Single(rig.Uploader.Received);
            Assert.Equal(2, rig.Uploader.Received[0].Count);

            sut.Stop();
        }

        // ================================================================= Helpers

        private static ISignalLogWriter GetSignalLog(EnrollmentOrchestrator sut)
        {
            var field = typeof(EnrollmentOrchestrator).GetField(
                "_signalLog",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return (ISignalLogWriter)field!.GetValue(sut)!;
        }

        private static IDeadlineScheduler GetScheduler(EnrollmentOrchestrator sut)
        {
            var field = typeof(EnrollmentOrchestrator).GetField(
                "_scheduler",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return (IDeadlineScheduler)field!.GetValue(sut)!;
        }

        private static DrainResult InvokeDrain(EnrollmentOrchestrator sut)
        {
            var method = typeof(EnrollmentOrchestrator).GetMethod(
                "DrainAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var task = (System.Threading.Tasks.Task<DrainResult>)method!.Invoke(sut, new object?[] { CancellationToken.None })!;
            return task.GetAwaiter().GetResult();
        }
    }
}
