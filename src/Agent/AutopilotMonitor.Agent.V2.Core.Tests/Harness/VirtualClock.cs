using System;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.DecisionCore.Engine;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Harness
{
    /// <summary>
    /// Deterministic <see cref="IClock"/> for V2.Core tests.
    /// 1:1-Copy aus <c>DecisionCore.Tests/Harness/VirtualClock.cs</c> — Plan §2.16
    /// Copy-Not-Share-Prinzip zwischen Testprojekten (konsistent zur V2-Isolation).
    /// </summary>
    public sealed class VirtualClock : IClock
    {
        private DateTime _now;

        public VirtualClock(DateTime initialUtcNow)
        {
            if (initialUtcNow.Kind != DateTimeKind.Utc && initialUtcNow.Kind != DateTimeKind.Unspecified)
            {
                throw new ArgumentException("VirtualClock expects UTC/unspecified DateTime.", nameof(initialUtcNow));
            }
            _now = DateTime.SpecifyKind(initialUtcNow, DateTimeKind.Utc);
        }

        public DateTime UtcNow => _now;

        public Task Delay(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Advance(delay);
            return Task.CompletedTask;
        }

        public void SetUtcNow(DateTime utcNow)
        {
            utcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
            if (utcNow < _now)
            {
                throw new InvalidOperationException(
                    $"VirtualClock cannot go backwards (current={_now:O}, requested={utcNow:O}).");
            }
            _now = utcNow;
        }

        public void Advance(TimeSpan delta)
        {
            if (delta < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(delta), "Advance delta must be non-negative.");
            }
            _now = _now.Add(delta);
        }
    }
}
