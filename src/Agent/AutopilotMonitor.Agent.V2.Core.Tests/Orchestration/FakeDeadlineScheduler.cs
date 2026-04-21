using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// Test fake — records Schedule/Cancel calls and can be scripted to throw on the next call.
    /// Avoids the real <see cref="DeadlineScheduler"/>'s timer plumbing for tests that don't
    /// need it.
    /// </summary>
    internal sealed class FakeDeadlineScheduler : IDeadlineScheduler
    {
        private readonly List<ActiveDeadline> _scheduled = new List<ActiveDeadline>();
        private readonly List<string> _cancelled = new List<string>();

        public Exception? ThrowOnSchedule { get; set; }
        public Exception? ThrowOnCancel { get; set; }

        public IReadOnlyList<ActiveDeadline> Scheduled => _scheduled;
        public IReadOnlyList<string> Cancelled => _cancelled;

        public event EventHandler<DeadlineFiredEventArgs>? Fired;

        public IReadOnlyList<ActiveDeadline> ActiveDeadlines => _scheduled;

        public void Schedule(ActiveDeadline deadline)
        {
            if (ThrowOnSchedule != null) throw ThrowOnSchedule;
            _scheduled.Add(deadline);
        }

        public void Cancel(string name)
        {
            if (ThrowOnCancel != null) throw ThrowOnCancel;
            _cancelled.Add(name);
        }

        public bool IsScheduled(string name)
        {
            foreach (var d in _scheduled) if (d.Name == name) return true;
            return false;
        }

        public void RehydrateFromSnapshot(IEnumerable<ActiveDeadline> deadlines)
        {
            foreach (var d in deadlines) Schedule(d);
        }

        public void Dispose() { /* no real timer to dispose */ }

        /// <summary>Test helper — raises the <see cref="Fired"/> event with given args.</summary>
        public void RaiseFired(ActiveDeadline deadline, DateTime firedAtUtc) =>
            Fired?.Invoke(this, new DeadlineFiredEventArgs(deadline, firedAtUtc));
    }
}
