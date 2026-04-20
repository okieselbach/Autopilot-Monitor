using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    public sealed class SessionTraceOrdinalProviderTests
    {
        [Fact]
        public void Default_seed_emits_zero_then_increments()
        {
            var p = new SessionTraceOrdinalProvider();
            Assert.Equal(-1, p.LastAssigned);

            Assert.Equal(0, p.Next());
            Assert.Equal(1, p.Next());
            Assert.Equal(2, p.Next());
            Assert.Equal(2, p.LastAssigned);
        }

        [Fact]
        public void Custom_seed_continues_from_there()
        {
            // Recovery: höchster persistierter Ordinal = 41 → Next() = 42.
            var p = new SessionTraceOrdinalProvider(seedLastAssigned: 41);
            Assert.Equal(41, p.LastAssigned);
            Assert.Equal(42, p.Next());
            Assert.Equal(43, p.Next());
        }

        [Fact]
        public async Task Next_is_thread_safe_and_monotonic_under_concurrent_callers()
        {
            const int Threads = 8;
            const int PerThread = 5000;
            var p = new SessionTraceOrdinalProvider();

            var tasks = new Task<long[]>[Threads];
            for (int t = 0; t < Threads; t++)
            {
                tasks[t] = Task.Run(() =>
                {
                    var buf = new long[PerThread];
                    for (int i = 0; i < PerThread; i++) buf[i] = p.Next();
                    return buf;
                });
            }

            var results = await Task.WhenAll(tasks);

            // Kein Wert doppelt, alle im Bereich [0, Threads*PerThread-1], lückenlos.
            var all = new System.Collections.Generic.HashSet<long>();
            foreach (var buf in results)
            {
                foreach (var v in buf)
                {
                    Assert.True(all.Add(v), $"Duplicate value {v}");
                }
            }

            Assert.Equal(Threads * PerThread, all.Count);
            Assert.Equal(Threads * PerThread - 1, p.LastAssigned);
        }
    }
}
