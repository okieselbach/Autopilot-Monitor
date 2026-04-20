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
