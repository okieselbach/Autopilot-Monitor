using System;

namespace AutopilotMonitor.DecisionCore.Engine
{
    /// <summary>
    /// Guard-evaluation record emitted during a reducer step. Plan §2.5 / §2.8 (Journal
    /// <c>GuardsJson</c>). A taken transition may have zero or more guard evaluations;
    /// a dead-end transition has at least one <c>Passed=false</c> guard that explains
    /// the rejection.
    /// </summary>
    public sealed class GuardReport
    {
        public GuardReport(string guardId, bool passed, string? reason = null)
        {
            if (string.IsNullOrEmpty(guardId))
            {
                throw new ArgumentException("GuardId is mandatory.", nameof(guardId));
            }

            GuardId = guardId;
            Passed = passed;
            Reason = reason;
        }

        public string GuardId { get; }

        public bool Passed { get; }

        public string? Reason { get; }
    }
}
