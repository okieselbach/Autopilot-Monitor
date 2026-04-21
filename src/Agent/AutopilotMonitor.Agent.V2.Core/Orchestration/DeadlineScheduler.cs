#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// <see cref="IDeadlineScheduler"/> backed by <see cref="System.Threading.Timer"/>.
    /// Plan §2.6 / L.7 — the sole owner of decision-relevant timers in V2.
    /// <para>
    /// Uses <see cref="IClock.UtcNow"/> to compute <c>remaining = DueAtUtc - UtcNow</c>.
    /// The firing itself is wall-clock driven (<see cref="Timer"/>), not virtual — virtual-clock
    /// tests must use past-due deadlines (fired synchronously on <see cref="ThreadPool"/>) or
    /// short real delays to assert behavior.
    /// </para>
    /// </summary>
    public sealed class DeadlineScheduler : IDeadlineScheduler
    {
        private readonly IClock _clock;
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly object _lock = new object();
        private bool _disposed;

        public DeadlineScheduler(IClock clock)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public event EventHandler<DeadlineFiredEventArgs>? Fired;

        public IReadOnlyList<ActiveDeadline> ActiveDeadlines
        {
            get
            {
                lock (_lock)
                {
                    var list = new List<ActiveDeadline>(_entries.Count);
                    foreach (var e in _entries.Values)
                    {
                        list.Add(e.Deadline);
                    }
                    return list;
                }
            }
        }

        public bool IsScheduled(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            lock (_lock)
            {
                return _entries.ContainsKey(name);
            }
        }

        public void Schedule(ActiveDeadline deadline)
        {
            if (deadline == null) throw new ArgumentNullException(nameof(deadline));

            Entry? newEntry = null;
            Entry? oldEntry = null;

            lock (_lock)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(DeadlineScheduler));

                if (_entries.TryGetValue(deadline.Name, out var existing))
                {
                    oldEntry = existing;
                    _entries.Remove(deadline.Name);
                }

                newEntry = new Entry(deadline);
                _entries[deadline.Name] = newEntry;
            }

            // Dispose old timer outside the lock — disposal can block while a pending
            // callback unwinds, and we don't want to hold _lock during that.
            oldEntry?.DisposeTimer();

            var remaining = deadline.DueAtUtc - _clock.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                // Past-due — fire on the ThreadPool. Don't fire inline: re-entrant Schedule
                // calls from subscribers would deadlock on _lock otherwise.
                ThreadPool.QueueUserWorkItem(_ => FireIfStillCurrent(newEntry));
            }
            else
            {
                var timer = new Timer(
                    static state => ((DeadlineScheduler)((TimerState)state!).Scheduler)
                        .FireIfStillCurrent(((TimerState)state).Entry),
                    new TimerState(this, newEntry),
                    remaining,
                    Timeout.InfiniteTimeSpan);
                newEntry.Timer = timer;
            }
        }

        public void Cancel(string name)
        {
            if (string.IsNullOrEmpty(name)) return;

            Entry? entry = null;
            lock (_lock)
            {
                if (_disposed) return;
                if (_entries.TryGetValue(name, out var existing))
                {
                    entry = existing;
                    _entries.Remove(name);
                }
            }

            entry?.DisposeTimer();
        }

        public void RehydrateFromSnapshot(IEnumerable<ActiveDeadline> deadlines)
        {
            if (deadlines == null) throw new ArgumentNullException(nameof(deadlines));
            foreach (var d in deadlines)
            {
                Schedule(d);
            }
        }

        public void Dispose()
        {
            List<Entry>? entriesToDispose = null;

            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                entriesToDispose = new List<Entry>(_entries.Values);
                _entries.Clear();
            }

            if (entriesToDispose == null) return;
            foreach (var e in entriesToDispose)
            {
                e.DisposeTimer();
            }
        }

        private void FireIfStillCurrent(Entry entry)
        {
            // Remove atomically; if Cancel or a newer Schedule already replaced us, skip.
            lock (_lock)
            {
                if (_disposed) return;
                if (!_entries.TryGetValue(entry.Deadline.Name, out var current)) return;
                if (!ReferenceEquals(current, entry)) return;
                _entries.Remove(entry.Deadline.Name);
            }

            entry.DisposeTimer();
            Fired?.Invoke(this, new DeadlineFiredEventArgs(entry.Deadline, _clock.UtcNow));
        }

        private sealed class Entry
        {
            public Entry(ActiveDeadline deadline)
            {
                Deadline = deadline;
            }

            public ActiveDeadline Deadline { get; }
            public Timer? Timer { get; set; }

            public void DisposeTimer()
            {
                var t = Timer;
                Timer = null;
                try { t?.Dispose(); }
                catch { /* Timer.Dispose may race with callback; we've already removed the entry. */ }
            }
        }

        private sealed class TimerState
        {
            public TimerState(DeadlineScheduler scheduler, Entry entry)
            {
                Scheduler = scheduler;
                Entry = entry;
            }

            public DeadlineScheduler Scheduler { get; }
            public Entry Entry { get; }
        }
    }
}
