using System;
using System.IO;
using System.Linq;
using System.Text;
using AutopilotMonitor.Agent.V2.Core.Persistence;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Persistence
{
    public sealed class SnapshotPersistenceTests
    {
        private static DecisionState BuildState(string sessionId = "S1", int stepIndex = 7)
        {
            var builder = DecisionState.CreateInitial(sessionId, "tenant-1")
                .ToBuilder()
                .WithStage(SessionStage.AwaitingHello)
                .WithLastAppliedSignalOrdinal(10)
                .WithStepIndex(stepIndex);

            builder.HelloResolvedUtc = new SignalFact<DateTime>(
                new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc), 5);

            return builder.Build();
        }

        [Fact]
        public void Save_then_Load_roundtrips_state()
        {
            using var tmp = new TempDirectory();
            var path = tmp.File("snapshot.json");
            var sut = new SnapshotPersistence(path);

            var original = BuildState();
            sut.Save(original);

            var loaded = sut.Load();
            Assert.NotNull(loaded);
            Assert.Equal(original.SessionId, loaded!.SessionId);
            Assert.Equal(original.TenantId, loaded.TenantId);
            Assert.Equal(SessionStage.AwaitingHello, loaded.Stage);
            Assert.Equal(10, loaded.LastAppliedSignalOrdinal);
            Assert.Equal(7, loaded.StepIndex);
            Assert.NotNull(loaded.HelloResolvedUtc);
            Assert.Equal(5, loaded.HelloResolvedUtc!.SourceSignalOrdinal);
        }

        [Fact]
        public void Load_returns_null_when_file_missing()
        {
            using var tmp = new TempDirectory();
            var sut = new SnapshotPersistence(tmp.File("does-not-exist.json"));
            Assert.Null(sut.Load());
        }

        [Fact]
        public void Load_returns_null_when_checksum_does_not_match()
        {
            using var tmp = new TempDirectory();
            var path = tmp.File("snapshot.json");
            var sut = new SnapshotPersistence(path);
            sut.Save(BuildState());

            // Tamper with the inner state JSON without recomputing the checksum.
            var content = File.ReadAllText(path, Encoding.UTF8);
            var tampered = content.Replace("tenant-1", "tenant-X");
            File.WriteAllText(path, tampered, Encoding.UTF8);

            Assert.Null(sut.Load());
        }

        [Fact]
        public void Load_returns_null_when_envelope_is_malformed()
        {
            using var tmp = new TempDirectory();
            var path = tmp.File("snapshot.json");
            File.WriteAllText(path, "this is not json at all", Encoding.UTF8);

            var sut = new SnapshotPersistence(path);
            Assert.Null(sut.Load());
        }

        [Fact]
        public void Quarantine_moves_file_into_timestamped_bucket_with_reason()
        {
            var fixedUtc = new DateTime(2026, 4, 20, 14, 30, 45, 123, DateTimeKind.Utc);
            using var tmp = new TempDirectory();
            var path = tmp.File("snapshot.json");
            var sut = new SnapshotPersistence(path, () => fixedUtc);

            sut.Save(BuildState());
            Assert.True(File.Exists(path));

            sut.Quarantine("checksum-mismatch");

            Assert.False(File.Exists(path));

            var quarantineDir = Path.Combine(tmp.Path, ".quarantine");
            Assert.True(Directory.Exists(quarantineDir));
            var bucket = Directory.EnumerateDirectories(quarantineDir).Single();
            Assert.Contains("20260420T143045123Z", bucket);

            Assert.True(File.Exists(Path.Combine(bucket, "snapshot.json")));
            Assert.Equal("checksum-mismatch", File.ReadAllText(Path.Combine(bucket, "reason.txt")));
        }

        [Fact]
        public void Save_is_atomic_via_tempfile_no_half_written_snapshot_visible()
        {
            // Plan §2.7 — Save() tempfile+rename pattern. After Save, the on-disk file is
            // either the new snapshot or absent; never a partial byte-sequence of the new one.
            using var tmp = new TempDirectory();
            var path = tmp.File("snapshot.json");
            var sut = new SnapshotPersistence(path);

            sut.Save(BuildState("S1"));
            var firstBytes = File.ReadAllBytes(path).Length;
            Assert.True(firstBytes > 0);

            sut.Save(BuildState("S2"));

            // Old .tmp file must be gone (rename consumed it).
            Assert.False(File.Exists(path + ".tmp"));

            // Loaded snapshot reflects the latest save — no partial overwrite.
            var loaded = sut.Load();
            Assert.Equal("S2", loaded!.SessionId);
        }

        // ============================================================================
        // TryReadRaw — Death-Rattle path (Plan §B, 2026-05-03). Quarantine-free static
        // reader; behaviour-equivalent failure modes to Load() via the shared
        // TryReadInternal parse path.
        // ============================================================================

        [Fact]
        public void TryReadRaw_returns_state_for_valid_envelope()
        {
            using var tmp = new TempDirectory();
            var path = tmp.File("snapshot.json");
            new SnapshotPersistence(path).Save(BuildState());

            var loaded = SnapshotPersistence.TryReadRaw(path);

            Assert.NotNull(loaded);
            Assert.Equal("S1", loaded!.SessionId);
            Assert.Equal(SessionStage.AwaitingHello, loaded.Stage);
            Assert.Equal(7, loaded.StepIndex);
        }

        [Fact]
        public void TryReadRaw_returns_null_when_file_missing()
        {
            using var tmp = new TempDirectory();
            Assert.Null(SnapshotPersistence.TryReadRaw(tmp.File("nope.json")));
        }

        [Fact]
        public void TryReadRaw_returns_null_when_checksum_does_not_match()
        {
            using var tmp = new TempDirectory();
            var path = tmp.File("snapshot.json");
            new SnapshotPersistence(path).Save(BuildState());

            var content = File.ReadAllText(path, Encoding.UTF8);
            File.WriteAllText(path, content.Replace("tenant-1", "tenant-X"), Encoding.UTF8);

            Assert.Null(SnapshotPersistence.TryReadRaw(path));
        }

        [Fact]
        public void TryReadRaw_returns_null_when_envelope_malformed()
        {
            using var tmp = new TempDirectory();
            var path = tmp.File("snapshot.json");
            File.WriteAllText(path, "<not json>", Encoding.UTF8);

            Assert.Null(SnapshotPersistence.TryReadRaw(path));
        }

        [Fact]
        public void TryReadRaw_returns_null_for_null_path()
        {
            Assert.Null(SnapshotPersistence.TryReadRaw(null!));
        }

        [Fact]
        public void TryReadRaw_returns_null_for_empty_path()
        {
            Assert.Null(SnapshotPersistence.TryReadRaw(string.Empty));
        }

        [Fact]
        public void TryReadRaw_does_NOT_quarantine_the_file_on_corruption()
        {
            // Critical contract for Death-Rattle: TryReadRaw must NEVER move/delete the
            // snapshot. The orchestrator's Start() owns the quarantine decision; if
            // TryReadRaw quarantined on a checksum miss, Start() would then see no file
            // and start a fresh-recovery branch instead of its own quarantine path.
            using var tmp = new TempDirectory();
            var path = tmp.File("snapshot.json");
            new SnapshotPersistence(path).Save(BuildState());

            var content = File.ReadAllText(path, Encoding.UTF8);
            File.WriteAllText(path, content.Replace("tenant-1", "tenant-X"), Encoding.UTF8);

            var result = SnapshotPersistence.TryReadRaw(path);

            Assert.Null(result);
            Assert.True(File.Exists(path), "TryReadRaw must leave a corrupt snapshot file in place — quarantine is the orchestrator's job.");
            Assert.False(Directory.Exists(Path.Combine(tmp.Path, ".quarantine")),
                "TryReadRaw must not create a .quarantine directory — it's a read-only static API.");
        }

        [Fact]
        public void TryReadRaw_yields_same_result_as_Load_for_valid_snapshot()
        {
            // Reader-parity test: TryReadRaw and Load share TryReadInternal, so observable
            // results must match for any well-formed snapshot. Pins the contract that
            // future refactors of the static reader cannot drift from the instance reader.
            using var tmp = new TempDirectory();
            var path = tmp.File("snapshot.json");
            var instance = new SnapshotPersistence(path);
            instance.Save(BuildState(stepIndex: 42));

            var viaLoad = instance.Load();
            var viaStatic = SnapshotPersistence.TryReadRaw(path);

            Assert.NotNull(viaLoad);
            Assert.NotNull(viaStatic);
            Assert.Equal(viaLoad!.SessionId, viaStatic!.SessionId);
            Assert.Equal(viaLoad.TenantId, viaStatic.TenantId);
            Assert.Equal(viaLoad.Stage, viaStatic.Stage);
            Assert.Equal(viaLoad.StepIndex, viaStatic.StepIndex);
            Assert.Equal(viaLoad.LastAppliedSignalOrdinal, viaStatic.LastAppliedSignalOrdinal);
            Assert.Equal(
                viaLoad.HelloResolvedUtc?.SourceSignalOrdinal,
                viaStatic.HelloResolvedUtc?.SourceSignalOrdinal);
        }

        [Fact]
        public void Roundtrip_preserves_active_deadlines_list()
        {
            using var tmp = new TempDirectory();
            var sut = new SnapshotPersistence(tmp.File("snapshot.json"));

            var stateWithDeadlines = DecisionState.CreateInitial("S1", "T1")
                .ToBuilder()
                .AddDeadline(new ActiveDeadline(
                    name: "hello_safety",
                    dueAtUtc: new DateTime(2026, 4, 20, 10, 30, 0, DateTimeKind.Utc),
                    firesSignalKind: DecisionSignalKind.DeadlineFired))
                .Build();

            sut.Save(stateWithDeadlines);

            var loaded = sut.Load()!;
            Assert.Single(loaded.Deadlines);
            Assert.Equal("hello_safety", loaded.Deadlines[0].Name);
            Assert.Equal(DecisionSignalKind.DeadlineFired, loaded.Deadlines[0].FiresSignalKind);
        }
    }
}
