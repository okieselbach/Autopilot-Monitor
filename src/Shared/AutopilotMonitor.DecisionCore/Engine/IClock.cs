using System;
using System.Threading;
using System.Threading.Tasks;

namespace AutopilotMonitor.DecisionCore.Engine
{
    /// <summary>
    /// Pluggable clock abstraction. Plan §2.6 L.7.
    /// <para>
    /// Time is consumed exclusively through this interface by every time-dependent
    /// component in <c>DecisionCore</c> and <c>Agent.V2.Core</c> (Deadline-Scheduler,
    /// ClassifierTick-Timer, recovery logic, …). Agent runtime uses <see cref="SystemClock"/>;
    /// the replay harness uses <c>VirtualClock</c> so 420-second timeouts run in
    /// milliseconds and are deterministic.
    /// </para>
    /// </summary>
    public interface IClock
    {
        /// <summary>Current UTC time.</summary>
        DateTime UtcNow { get; }

        /// <summary>Async delay; virtualized in tests.</summary>
        Task Delay(TimeSpan delay, CancellationToken cancellationToken = default);
    }
}
