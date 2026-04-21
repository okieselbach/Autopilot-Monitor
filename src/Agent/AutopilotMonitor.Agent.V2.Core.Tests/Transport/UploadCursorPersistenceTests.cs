using System.IO;
using System.Text;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Transport.Telemetry;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Transport
{
    public sealed class UploadCursorPersistenceTests
    {
        [Fact]
        public void Load_on_missing_file_returns_minus_one()
        {
            using var tmp = new TempDirectory();
            var sut = new UploadCursorPersistence(tmp.File("cursor.json"));
            Assert.Equal(-1, sut.Load());
        }

        [Fact]
        public void Save_then_Load_roundtrips()
        {
            using var tmp = new TempDirectory();
            var sut = new UploadCursorPersistence(tmp.File("cursor.json"));

            sut.Save(42);
            Assert.Equal(42, sut.Load());
        }

        [Fact]
        public void Save_overwrites_previous_value_atomically()
        {
            using var tmp = new TempDirectory();
            var sut = new UploadCursorPersistence(tmp.File("cursor.json"));

            sut.Save(10);
            sut.Save(20);
            sut.Save(30);

            Assert.Equal(30, sut.Load());
            Assert.False(File.Exists(tmp.File("cursor.json.tmp")));
        }

        [Fact]
        public void Load_on_corrupt_file_returns_minus_one_not_throws()
        {
            using var tmp = new TempDirectory();
            var path = tmp.File("cursor.json");
            File.WriteAllText(path, "not valid json at all", Encoding.UTF8);

            var sut = new UploadCursorPersistence(path);
            Assert.Equal(-1, sut.Load());
        }
    }
}
