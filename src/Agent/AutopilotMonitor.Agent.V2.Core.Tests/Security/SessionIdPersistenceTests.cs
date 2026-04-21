using System;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Security
{
    public sealed class SessionIdPersistenceTests
    {
        [Fact]
        public void GetOrCreate_without_existing_file_creates_a_new_guid()
        {
            using var tmp = new TempDirectory();
            var sut = new SessionIdPersistence(tmp.Path);

            var id = sut.GetOrCreate();

            Assert.True(Guid.TryParse(id, out _));
            Assert.True(File.Exists(Path.Combine(tmp.Path, "session.id")));
            Assert.Equal(id, File.ReadAllText(Path.Combine(tmp.Path, "session.id")));
        }

        [Fact]
        public void GetOrCreate_returns_same_id_on_repeated_calls()
        {
            using var tmp = new TempDirectory();
            var sut = new SessionIdPersistence(tmp.Path);

            var first = sut.GetOrCreate();
            var second = sut.GetOrCreate();
            var third = sut.GetOrCreate();

            Assert.Equal(first, second);
            Assert.Equal(first, third);
        }

        [Fact]
        public void GetOrCreate_new_instance_resumes_persisted_session()
        {
            using var tmp = new TempDirectory();
            var first = new SessionIdPersistence(tmp.Path).GetOrCreate();
            var resumed = new SessionIdPersistence(tmp.Path).GetOrCreate();

            Assert.Equal(first, resumed);
        }

        [Fact]
        public void GetOrCreate_regenerates_when_file_content_is_corrupt()
        {
            using var tmp = new TempDirectory();
            File.WriteAllText(Path.Combine(tmp.Path, "session.id"), "not-a-guid");

            var id = new SessionIdPersistence(tmp.Path).GetOrCreate();

            Assert.True(Guid.TryParse(id, out _));
            Assert.NotEqual("not-a-guid", id);
        }

        [Fact]
        public void Delete_clears_persisted_session_so_next_call_regenerates()
        {
            using var tmp = new TempDirectory();
            var sut = new SessionIdPersistence(tmp.Path);
            var first = sut.GetOrCreate();

            sut.Delete();
            Assert.False(File.Exists(Path.Combine(tmp.Path, "session.id")));

            var second = sut.GetOrCreate();
            Assert.NotEqual(first, second);
        }

        [Fact]
        public void Ctor_creates_data_directory_when_missing()
        {
            using var tmp = new TempDirectory();
            var nested = Path.Combine(tmp.Path, "nested", "state");
            Assert.False(Directory.Exists(nested));

            var sut = new SessionIdPersistence(nested);
            var id = sut.GetOrCreate();

            Assert.True(Directory.Exists(nested));
            Assert.True(Guid.TryParse(id, out _));
        }

        [Fact]
        public void Ctor_rejects_null_or_empty_data_directory()
        {
            Assert.Throws<ArgumentNullException>(() => new SessionIdPersistence(null!));
            Assert.Throws<ArgumentNullException>(() => new SessionIdPersistence(""));
            Assert.Throws<ArgumentNullException>(() => new SessionIdPersistence("   "));
        }

        [Fact]
        public void GetOrCreate_with_logger_emits_resume_debug_on_second_call()
        {
            using var tmp = new TempDirectory();
            var logDir = Path.Combine(tmp.Path, "logs");
            var logger = new AgentLogger(logDir, AgentLogLevel.Debug);

            var first = new SessionIdPersistence(tmp.Path).GetOrCreate(logger);
            var second = new SessionIdPersistence(tmp.Path).GetOrCreate(logger);

            Assert.Equal(first, second);
        }

        // --------------------------------------------------------------- M4.6.α additions

        [Fact]
        public void GetOrCreate_also_writes_session_created_timestamp()
        {
            using var tmp = new TempDirectory();
            var sut = new SessionIdPersistence(tmp.Path);

            sut.GetOrCreate();

            var createdPath = Path.Combine(tmp.Path, "session.created");
            Assert.True(File.Exists(createdPath));
            var parsed = sut.LoadSessionCreatedAt();
            Assert.NotNull(parsed);
            Assert.Equal(DateTimeKind.Utc, parsed!.Value.Kind);
        }

        [Fact]
        public void LoadSessionCreatedAt_returns_null_when_file_missing()
        {
            using var tmp = new TempDirectory();
            Assert.Null(new SessionIdPersistence(tmp.Path).LoadSessionCreatedAt());
        }

        [Fact]
        public void LoadSessionCreatedAt_returns_null_when_file_is_corrupt()
        {
            using var tmp = new TempDirectory();
            File.WriteAllText(Path.Combine(tmp.Path, "session.created"), "not-a-date");
            Assert.Null(new SessionIdPersistence(tmp.Path).LoadSessionCreatedAt());
        }

        [Fact]
        public void GetOrCreate_initialises_missing_session_created_on_recovery()
        {
            using var tmp = new TempDirectory();

            // Simulate a session.id written by an older persistence without a companion timestamp.
            var existingId = Guid.NewGuid().ToString();
            File.WriteAllText(Path.Combine(tmp.Path, "session.id"), existingId);

            var sut = new SessionIdPersistence(tmp.Path);
            var resumed = sut.GetOrCreate();

            Assert.Equal(existingId, resumed);
            Assert.True(File.Exists(Path.Combine(tmp.Path, "session.created")));
            Assert.NotNull(sut.LoadSessionCreatedAt());
        }

        [Fact]
        public void SaveSessionCreatedAt_persists_utc_and_roundtrips()
        {
            using var tmp = new TempDirectory();
            var sut = new SessionIdPersistence(tmp.Path);

            var now = new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc);
            sut.SaveSessionCreatedAt(now);

            var loaded = sut.LoadSessionCreatedAt();
            Assert.Equal(now, loaded);
        }

        [Fact]
        public void SessionExists_reflects_session_id_presence()
        {
            using var tmp = new TempDirectory();
            var sut = new SessionIdPersistence(tmp.Path);

            Assert.False(sut.SessionExists());

            sut.GetOrCreate();
            Assert.True(sut.SessionExists());

            sut.Delete();
            Assert.False(sut.SessionExists());
        }

        [Fact]
        public void IsWhiteGloveResume_returns_true_when_marker_present()
        {
            using var tmp = new TempDirectory();
            var sut = new SessionIdPersistence(tmp.Path);

            Assert.False(sut.IsWhiteGloveResume());

            File.WriteAllText(Path.Combine(tmp.Path, "whiteglove.complete"), "1");
            Assert.True(sut.IsWhiteGloveResume());
        }

        [Fact]
        public void Delete_clears_session_id_created_timestamp_and_whiteglove_marker()
        {
            using var tmp = new TempDirectory();
            var sut = new SessionIdPersistence(tmp.Path);
            sut.GetOrCreate();
            File.WriteAllText(Path.Combine(tmp.Path, "whiteglove.complete"), "1");

            sut.Delete();

            Assert.False(File.Exists(Path.Combine(tmp.Path, "session.id")));
            Assert.False(File.Exists(Path.Combine(tmp.Path, "session.created")));
            Assert.False(File.Exists(Path.Combine(tmp.Path, "whiteglove.complete")));
        }
    }
}
