using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests.Harness
{
    public sealed class VirtualClockTests
    {
        [Fact]
        public void UtcNow_reflectsInitialValue()
        {
            var t0 = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);
            var clock = new VirtualClock(t0);
            Assert.Equal(t0, clock.UtcNow);
        }

        [Fact]
        public void Advance_increasesUtcNow()
        {
            var t0 = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);
            var clock = new VirtualClock(t0);

            clock.Advance(TimeSpan.FromMinutes(5));

            Assert.Equal(t0.AddMinutes(5), clock.UtcNow);
        }

        [Fact]
        public void Advance_negative_throws()
        {
            var clock = new VirtualClock(DateTime.UtcNow);
            Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(TimeSpan.FromMinutes(-1)));
        }

        [Fact]
        public void SetUtcNow_backwards_throws()
        {
            var t0 = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);
            var clock = new VirtualClock(t0);
            Assert.Throws<InvalidOperationException>(() => clock.SetUtcNow(t0.AddMinutes(-1)));
        }

        [Fact]
        public async Task Delay_completesImmediately_AndAdvancesClock()
        {
            var t0 = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);
            var clock = new VirtualClock(t0);
            var expectedNow = t0.AddHours(7);

            // In real wall-clock this would take 7 hours; VirtualClock returns instantly.
            var start = DateTime.UtcNow;
            await clock.Delay(TimeSpan.FromHours(7));
            var elapsed = DateTime.UtcNow - start;

            Assert.Equal(expectedNow, clock.UtcNow);
            Assert.True(elapsed < TimeSpan.FromSeconds(1),
                $"VirtualClock.Delay should be instant, took {elapsed.TotalMilliseconds} ms.");
        }

        [Fact]
        public async Task Delay_withCancelledToken_throwsOperationCanceled()
        {
            var clock = new VirtualClock(DateTime.UtcNow);
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() =>
                    clock.Delay(TimeSpan.FromSeconds(1), cts.Token));
            }
        }
    }
}
