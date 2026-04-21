using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Telemetry.Events;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Telemetry.Events
{
    public sealed class EventSequenceCounterTests
    {
        [Fact]
        public void First_Next_returns_one_on_empty_persistence()
        {
            using var tmp = new TempDirectory();
            var p = new EventSequencePersistence(tmp.File("seq.json"));
            var c = new EventSequenceCounter(p);

            Assert.Equal(0, c.LastAssigned);
            Assert.Equal(1, c.Next());
            Assert.Equal(2, c.Next());
            Assert.Equal(3, c.Next());
            Assert.Equal(3, c.LastAssigned);
        }

        [Fact]
        public void Counter_seeds_from_persistence_on_recovery()
        {
            using var tmp = new TempDirectory();
            var path = tmp.File("seq.json");
            new EventSequencePersistence(path).Save(99);

            var c = new EventSequenceCounter(new EventSequencePersistence(path));
            Assert.Equal(99, c.LastAssigned);
            Assert.Equal(100, c.Next());
        }

        [Fact]
        public void Next_persists_every_increment()
        {
            using var tmp = new TempDirectory();
            var path = tmp.File("seq.json");
            var p = new EventSequencePersistence(path);
            var c = new EventSequenceCounter(p);

            c.Next();
            c.Next();
            c.Next();

            Assert.Equal(3, new EventSequencePersistence(path).Load());
        }

        [Fact]
        public async Task Next_is_thread_safe_under_concurrent_callers()
        {
            using var tmp = new TempDirectory();
            var p = new EventSequencePersistence(tmp.File("seq.json"));
            var c = new EventSequenceCounter(p);

            // Every Next() persists to disk — stay below the level where parallel xUnit tests
            // contend on the Windows atomic-rename path. Thread-safety is exercised by the
            // parallel callers; the uniqueness assertion below catches any race.
            const int Threads = 4;
            const int PerThread = 50;
            var tasks = new Task<long[]>[Threads];
            for (int t = 0; t < Threads; t++)
            {
                tasks[t] = Task.Run(() =>
                {
                    var buf = new long[PerThread];
                    for (int i = 0; i < PerThread; i++) buf[i] = c.Next();
                    return buf;
                });
            }

            var results = await Task.WhenAll(tasks);

            var all = new System.Collections.Generic.HashSet<long>();
            foreach (var buf in results)
                foreach (var v in buf)
                    Assert.True(all.Add(v));

            Assert.Equal(Threads * PerThread, all.Count);
            Assert.Equal(Threads * PerThread, c.LastAssigned);
        }
    }
}
