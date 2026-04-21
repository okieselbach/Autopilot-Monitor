#nullable enable
using System;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Payload for <see cref="IDeadlineScheduler.Fired"/>. Plan §2.6.
    /// <para>
    /// The orchestrator consumes this event and translates it into a synthetic
    /// <c>DeadlineFired</c>-<see cref="AutopilotMonitor.DecisionCore.Signals.DecisionSignal"/>
    /// whose <c>OccurredAtUtc</c> equals the deadline's <see cref="ActiveDeadline.DueAtUtc"/>,
    /// not the wall-clock firing time — so replay stays deterministic even if the agent
    /// restarts long after the deadline has passed.
    /// </para>
    /// </summary>
    public sealed class DeadlineFiredEventArgs : EventArgs
    {
        public DeadlineFiredEventArgs(ActiveDeadline deadline, DateTime firedAtUtc)
        {
            Deadline = deadline ?? throw new ArgumentNullException(nameof(deadline));
            FiredAtUtc = firedAtUtc;
        }

        public ActiveDeadline Deadline { get; }

        /// <summary>Wall-clock time the event was raised. For observability; does NOT influence the signal's OccurredAtUtc.</summary>
        public DateTime FiredAtUtc { get; }
    }
}
