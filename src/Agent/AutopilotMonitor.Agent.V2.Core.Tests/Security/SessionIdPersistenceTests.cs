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
    }
}
