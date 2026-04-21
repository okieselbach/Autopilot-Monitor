using System;
using System.Collections.Generic;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    [Collection("SerialThreading")]
    public sealed class DeadlineSchedulerTests
    {
        // 5000ms cushion: 2000ms was racy under full-suite parallelism even after adding
        // [Collection("SerialThreading")] — timer callbacks execute on the shared ThreadPool
        // and can lag when the pool is saturated by unrelated test classes. Plan §4.x M4.5.c.
        private const int DefaultWaitMs = 5000;

        private static ActiveDeadline Deadline(string name, DateTime dueAt) =>
            new ActiveDeadline(name, dueAt, DecisionSignalKind.DeadlineFired);

        [Fact]
        public void Past_due_deadline_fires_immediately_on_ThreadPool()
        {
            var now = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);
            var clock = new VirtualClock(now);
            using var sched = new DeadlineScheduler(clock);

            using var fired = new ManualResetEventSlim(false);
            ActiveDeadline? seen = null;
            sched.Fired += (_, args) => { seen = args.Deadline; fired.Set(); };

            var pastDue = Deadline("hello_safety", now.AddSeconds(-30));
            sched.Schedule(pastDue);

            Assert.True(fired.Wait(DefaultWaitMs), "Past-due deadline did not fire.");
            Assert.Equal("hello_safety", seen!.Name);
            Assert.False(sched.IsScheduled("hello_safety"));
        }

        [Fact]
        public void Future_deadline_fires_after_elapsed_time()
        {
            var now = DateTime.UtcNow;
            // Use SystemClock for a real wait so the wall-clock Timer ticks.
            var clock = new AutopilotMonitor.DecisionCore.Engine.SystemClock();
            using var sched = new DeadlineScheduler(clock);

            using var fired = new ManualResetEventSlim(false);
            sched.Fired += (_, _) => fired.Set();

            var deadline = Deadline("short", DateTime.UtcNow.AddMilliseconds(80));
            sched.Schedule(deadline);
            Assert.True(sched.IsScheduled("short"));

            Assert.True(fired.Wait(DefaultWaitMs), "Future deadline did not fire.");
            Assert.False(sched.IsScheduled("short"));
        }

        [Fact]
        public void Cancel_prevents_future_fire()
        {
            var clock = new AutopilotMonitor.DecisionCore.Engine.SystemClock();
            using var sched = new DeadlineScheduler(clock);

            int fireCount = 0;
            sched.Fired += (_, _) => Interlocked.Increment(ref fireCount);

            sched.Schedule(Deadline("cancel-me", DateTime.UtcNow.AddMilliseconds(200)));
            Assert.True(sched.IsScheduled("cancel-me"));

            sched.Cancel("cancel-me");
            Assert.False(sched.IsScheduled("cancel-me"));

            Thread.Sleep(400);
            Assert.Equal(0, fireCount);
        }

        [Fact]
        public void Cancel_unknown_name_is_noop()
        {
            using var sched = new DeadlineScheduler(new AutopilotMonitor.DecisionCore.Engine.SystemClock());
            sched.Cancel("never-scheduled");
            sched.Cancel(string.Empty);
            // Just assert no exception.
        }

        [Fact]
        public void Reschedule_same_name_replaces_old_timer()
        {
            // Plan §2.6 Schedule-semantics: same-name replaces.
            var clock = new AutopilotMonitor.DecisionCore.Engine.SystemClock();
            using var sched = new DeadlineScheduler(clock);

            var firedNames = new List<ActiveDeadline>();
            using var someFired = new ManualResetEventSlim(false);
            sched.Fired += (_, args) =>
            {
                lock (firedNames) firedNames.Add(args.Deadline);
                someFired.Set();
            };

            // First: far in the future (won't fire within the test window).
            sched.Schedule(Deadline("classifier_tick", DateTime.UtcNow.AddSeconds(30)));
            // Replace with past-due. Old timer must be cancelled before new fires.
            var replacement = Deadline("classifier_tick", DateTime.UtcNow.AddSeconds(-1));
            sched.Schedule(replacement);

            Assert.True(someFired.Wait(DefaultWaitMs));

            lock (firedNames)
            {
                Assert.Single(firedNames);
                Assert.Equal("classifier_tick", firedNames[0].Name);
                // Replacement's DueAtUtc differs from the original — prove the replacement fired.
                Assert.Equal(replacement.DueAtUtc, firedNames[0].DueAtUtc);
            }
        }

        [Fact]
        public void ActiveDeadlines_snapshot_reflects_current_schedule()
        {
            var clock = new AutopilotMonitor.DecisionCore.Engine.SystemClock();
            using var sched = new DeadlineScheduler(clock);

            sched.Schedule(Deadline("a", DateTime.UtcNow.AddSeconds(30)));
            sched.Schedule(Deadline("b", DateTime.UtcNow.AddSeconds(30)));

            var snap = sched.ActiveDeadlines;
            Assert.Equal(2, snap.Count);

            sched.Cancel("a");
            Assert.Single(sched.ActiveDeadlines);
        }

        [Fact]
        public void RehydrateFromSnapshot_reschedules_each_deadline_past_due_fires_immediately()
        {
            // Plan §2.6 Restart-Recovery: past-due → sofort DeadlineFired.
            var now = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);
            var clock = new VirtualClock(now);
            using var sched = new DeadlineScheduler(clock);

            int fireCount = 0;
            using var allFired = new CountdownEvent(2);
            sched.Fired += (_, _) =>
            {
                Interlocked.Increment(ref fireCount);
                allFired.Signal();
            };

            sched.RehydrateFromSnapshot(new[]
            {
                Deadline("expired_1", now.AddMinutes(-10)),
                Deadline("expired_2", now.AddSeconds(-1)),
            });

            Assert.True(allFired.Wait(DefaultWaitMs));
            Assert.Equal(2, fireCount);
            Assert.Empty(sched.ActiveDeadlines);
        }

        [Fact]
        public void Dispose_cancels_all_pending_timers()
        {
            var clock = new AutopilotMonitor.DecisionCore.Engine.SystemClock();
            var sched = new DeadlineScheduler(clock);

            int fireCount = 0;
            sched.Fired += (_, _) => Interlocked.Increment(ref fireCount);

            sched.Schedule(Deadline("a", DateTime.UtcNow.AddMilliseconds(150)));
            sched.Schedule(Deadline("b", DateTime.UtcNow.AddMilliseconds(150)));

            sched.Dispose();
            Thread.Sleep(350);

            Assert.Equal(0, fireCount);
        }

        [Fact]
        public void Schedule_after_Dispose_throws()
        {
            var sched = new DeadlineScheduler(new AutopilotMonitor.DecisionCore.Engine.SystemClock());
            sched.Dispose();

            Assert.Throws<ObjectDisposedException>(() =>
                sched.Schedule(Deadline("x", DateTime.UtcNow.AddSeconds(1))));
        }
    }
}
