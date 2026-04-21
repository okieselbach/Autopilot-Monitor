using System;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Security
{
    public sealed class AuthFailureTrackerTests
    {
        private static AgentLogger NewLogger()
        {
            var tmp = new TempDirectory();
            return new AgentLogger(System.IO.Path.Combine(tmp.Path, "logs"), AgentLogLevel.Debug);
        }

        // ============================================================= Count-based threshold

        [Fact]
        public void RecordFailure_fires_threshold_at_max_failures()
        {
            var clock = new FakeClock(new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc));
            var tracker = new AuthFailureTracker(maxFailures: 3, timeoutMinutes: 0, clock, NewLogger());

            AuthFailureThresholdEventArgs captured = null!;
            tracker.ThresholdExceeded += (_, e) => captured = e;

            tracker.RecordFailure(401, "config");
            tracker.RecordFailure(401, "config");
            Assert.Null(captured);

            tracker.RecordFailure(401, "config");
            Assert.NotNull(captured);
            Assert.Equal(3, captured.ConsecutiveFailures);
            Assert.Equal(401, captured.LastStatusCode);
            Assert.Equal("config", captured.LastOperation);
        }

        [Fact]
        public void RecordSuccess_resets_counter()
        {
            var clock = new FakeClock(new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc));
            var tracker = new AuthFailureTracker(maxFailures: 3, timeoutMinutes: 0, clock, NewLogger());

            int events = 0;
            tracker.ThresholdExceeded += (_, _) => events++;

            tracker.RecordFailure(401, "config");
            tracker.RecordFailure(401, "config");
            tracker.RecordSuccess();
            tracker.RecordFailure(403, "upload");
            tracker.RecordFailure(403, "upload");

            Assert.Equal(0, events);
            Assert.Equal(2, tracker.ConsecutiveFailures);
        }

        // ============================================================= Time-window threshold

        [Fact]
        public void RecordFailure_fires_threshold_when_time_window_exceeded()
        {
            var start = new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc);
            var clock = new FakeClock(start);
            var tracker = new AuthFailureTracker(maxFailures: 0, timeoutMinutes: 10, clock, NewLogger());

            int events = 0;
            AuthFailureThresholdEventArgs captured = null!;
            tracker.ThresholdExceeded += (_, e) => { events++; captured = e; };

            tracker.RecordFailure(401, "config");
            Assert.Equal(0, events);

            clock.Now = start.AddMinutes(5);
            tracker.RecordFailure(401, "config");
            Assert.Equal(0, events);

            clock.Now = start.AddMinutes(11);
            tracker.RecordFailure(401, "config");

            Assert.Equal(1, events);
            Assert.Equal(start, captured.FirstFailureUtc);
            Assert.Contains("time window", captured.Reason);
        }

        [Fact]
        public void RecordSuccess_resets_time_window_anchor()
        {
            var start = new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc);
            var clock = new FakeClock(start);
            var tracker = new AuthFailureTracker(maxFailures: 0, timeoutMinutes: 10, clock, NewLogger());

            int events = 0;
            tracker.ThresholdExceeded += (_, _) => events++;

            tracker.RecordFailure(401, "config");
            clock.Now = start.AddMinutes(9);
            tracker.RecordSuccess();

            // fresh window — 11 minutes later, first failure in this new window, no threshold.
            clock.Now = start.AddMinutes(20);
            tracker.RecordFailure(401, "config");

            Assert.Equal(0, events);
        }

        // ============================================================= Idempotence

        [Fact]
        public void ThresholdExceeded_fires_only_once_even_with_many_failures()
        {
            var clock = new FakeClock(new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc));
            var tracker = new AuthFailureTracker(maxFailures: 2, timeoutMinutes: 0, clock, NewLogger());

            int events = 0;
            tracker.ThresholdExceeded += (_, _) => events++;

            for (int i = 0; i < 10; i++) tracker.RecordFailure(401, "config");

            Assert.Equal(1, events);
        }

        [Fact]
        public void ThresholdExceeded_handler_exception_does_not_propagate()
        {
            var clock = new FakeClock(new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc));
            var tracker = new AuthFailureTracker(maxFailures: 1, timeoutMinutes: 0, clock, NewLogger());

            tracker.ThresholdExceeded += (_, _) => throw new InvalidOperationException("listener exploded");

            // Must not propagate — tracker is a best-effort observer.
            tracker.RecordFailure(401, "config");
        }

        // ============================================================= Disabled limits

        [Fact]
        public void RecordFailure_never_fires_when_both_limits_disabled()
        {
            var clock = new FakeClock(new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc));
            var tracker = new AuthFailureTracker(maxFailures: 0, timeoutMinutes: 0, clock, NewLogger());

            int events = 0;
            tracker.ThresholdExceeded += (_, _) => events++;

            for (int i = 0; i < 100; i++) tracker.RecordFailure(401, "op");

            Assert.Equal(0, events);
            Assert.Equal(100, tracker.ConsecutiveFailures);
        }

        // ============================================================= UpdateLimits (remote config override)

        [Fact]
        public void UpdateLimits_tightens_threshold_retroactively()
        {
            var clock = new FakeClock(new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc));
            var tracker = new AuthFailureTracker(maxFailures: 10, timeoutMinutes: 0, clock, NewLogger());

            int events = 0;
            tracker.ThresholdExceeded += (_, _) => events++;

            tracker.RecordFailure(401, "config");
            tracker.RecordFailure(401, "config");

            // Tenant tightened the policy — the next failure should now fire.
            tracker.UpdateLimits(maxFailures: 3, timeoutMinutes: 0);
            tracker.RecordFailure(401, "config");

            Assert.Equal(1, events);
        }

        [Fact]
        public void UpdateLimits_accepts_negative_max_as_disabled()
        {
            var clock = new FakeClock(new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc));
            var tracker = new AuthFailureTracker(maxFailures: -1, timeoutMinutes: 0, clock, NewLogger());

            int events = 0;
            tracker.ThresholdExceeded += (_, _) => events++;

            for (int i = 0; i < 20; i++) tracker.RecordFailure(401, "op");
            Assert.Equal(0, events);
        }

        // ============================================================= Thread-safety smoke

        [Fact]
        public void RecordFailure_is_safe_under_concurrent_callers()
        {
            var clock = new FakeClock(new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc));
            var tracker = new AuthFailureTracker(maxFailures: 50, timeoutMinutes: 0, clock, NewLogger());

            int events = 0;
            tracker.ThresholdExceeded += (_, _) => Interlocked.Increment(ref events);

            var threads = new Thread[8];
            for (int t = 0; t < threads.Length; t++)
            {
                threads[t] = new Thread(() =>
                {
                    for (int i = 0; i < 20; i++) tracker.RecordFailure(401, "concurrent");
                });
                threads[t].Start();
            }
            foreach (var th in threads) th.Join();

            // Many concurrent Increments cross the 50 ceiling — but ThresholdExceeded must fire exactly once.
            Assert.Equal(1, events);
        }

        // ============================================================= Guard

        [Fact]
        public void Ctor_throws_on_null_clock()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new AuthFailureTracker(1, 0, null!, NewLogger()));
        }

        [Fact]
        public void Ctor_throws_on_null_logger()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new AuthFailureTracker(1, 0, new FakeClock(DateTime.UtcNow), null!));
        }

        private sealed class FakeClock : AutopilotMonitor.DecisionCore.Engine.IClock
        {
            public DateTime Now { get; set; }
            public FakeClock(DateTime now) { Now = now; }
            public DateTime UtcNow => Now;
            public System.Threading.Tasks.Task Delay(TimeSpan delay, CancellationToken cancellationToken = default) => System.Threading.Tasks.Task.CompletedTask;
        }
    }
}
