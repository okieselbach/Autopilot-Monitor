using System.IO;
using System.Text;
using AutopilotMonitor.Agent.V2.Core.Telemetry.Events;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Telemetry.Events
{
    public sealed class EventSequencePersistenceTests
    {
        [Fact]
        public void Load_on_missing_file_returns_zero()
        {
            using var tmp = new TempDirectory();
            var p = new EventSequencePersistence(tmp.File("missing.json"));
            Assert.Equal(0, p.Load());
        }

        [Fact]
        public void Save_then_Load_roundtrips_value()
        {
            using var tmp = new TempDirectory();
            var path = tmp.File("seq.json");
            var p1 = new EventSequencePersistence(path);
            p1.Save(42);

            var p2 = new EventSequencePersistence(path);
            Assert.Equal(42, p2.Load());
        }

        [Fact]
        public void Save_overwrites_previous_value_atomically()
        {
            using var tmp = new TempDirectory();
            var path = tmp.File("seq.json");
            var p = new EventSequencePersistence(path);

            p.Save(10);
            p.Save(20);
            p.Save(30);

            Assert.Equal(30, p.Load());
            Assert.False(File.Exists(path + ".tmp"), "Temp file must not linger after rename.");
        }

        [Fact]
        public void Load_on_corrupt_json_returns_zero()
        {
            using var tmp = new TempDirectory();
            var path = tmp.File("seq.json");
            File.WriteAllText(path, "{not valid json", Encoding.UTF8);

            var p = new EventSequencePersistence(path);
            Assert.Equal(0, p.Load());
        }

        [Fact]
        public void Constructor_creates_missing_parent_directory()
        {
            using var tmp = new TempDirectory();
            var nested = Path.Combine(tmp.Path, "Telemetry", "sub", "seq.json");

            var p = new EventSequencePersistence(nested);
            p.Save(7);

            Assert.True(File.Exists(nested));
            Assert.Equal(7, p.Load());
        }
    }
}
