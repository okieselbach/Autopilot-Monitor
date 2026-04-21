using System;
using System.Threading;
using System.Threading.Tasks;

namespace AutopilotMonitor.DecisionCore.Engine
{
    /// <summary>
    /// Default <see cref="IClock"/> implementation for the agent runtime — wraps
    /// <see cref="DateTime.UtcNow"/> and <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
    /// Tests must use a virtual clock implementation, never this one.
    /// </summary>
    public sealed class SystemClock : IClock
    {
        public static readonly SystemClock Instance = new SystemClock();

        public DateTime UtcNow => DateTime.UtcNow;

        public Task Delay(TimeSpan delay, CancellationToken cancellationToken = default) =>
            Task.Delay(delay, cancellationToken);
    }
}
