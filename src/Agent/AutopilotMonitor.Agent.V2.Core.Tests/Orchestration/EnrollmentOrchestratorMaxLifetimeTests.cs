using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Transport;
using AutopilotMonitor.DecisionCore.Classifiers;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// M4.6.α — max-lifetime watchdog. When the configured lifetime elapses without a
    /// decision-terminal stage, the orchestrator raises <see cref="EnrollmentOrchestrator.Terminated"/>
    /// with reason <see cref="EnrollmentTerminationReason.MaxLifetimeExceeded"/> and outcome
    /// <see cref="EnrollmentTerminationOutcome.TimedOut"/>.
    /// </summary>
    [Collection("SerialThreading")]
    public sealed class EnrollmentOrchestratorMaxLifetimeTests
    {
        private static DateTime At => new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Terminated_fires_once_when_max_lifetime_elapses()
        {
            using var rig = new EnrollmentOrchestratorRig(At);
            var sut = rig.Build(agentMaxLifetime: TimeSpan.FromMilliseconds(200));

            using var fired = new ManualResetEventSlim(false);
            EnrollmentTerminatedEventArgs? captured = null;
            var fireCount = 0;
            sut.Terminated += (_, e) =>
            {
                Interlocked.Increment(ref fireCount);
                captured = e;
                fired.Set();
            };

            sut.Start();
            Assert.True(fired.Wait(5000), "Terminated event did not fire within 5s.");

            Assert.NotNull(captured);
            Assert.Equal(EnrollmentTerminationReason.MaxLifetimeExceeded, captured!.Reason);
            Assert.Equal(EnrollmentTerminationOutcome.TimedOut, captured.Outcome);
            Assert.NotNull(captured.StageName);
            Assert.NotNull(captured.Details);

            // Even if the timer fired multiple times (it shouldn't — it's one-shot), we only
            // raise the event once thanks to the Interlocked latch.
            Thread.Sleep(300);
            Assert.Equal(1, fireCount);

            sut.Stop();
        }

        [Fact]
        public void Terminated_not_fired_when_max_lifetime_is_null()
        {
            using var rig = new EnrollmentOrchestratorRig(At);
            var sut = rig.Build(agentMaxLifetime: null);

            var fireCount = 0;
            sut.Terminated += (_, _) => Interlocked.Increment(ref fireCount);

            sut.Start();
            Thread.Sleep(200);
            sut.Stop();

            Assert.Equal(0, fireCount);
        }

        [Theory]
        [InlineData(0)]   // TimeSpan.Zero — must reject
        [InlineData(-1)]  // negative ms — must reject
        public void Ctor_rejects_non_positive_max_lifetime(int milliseconds)
        {
            using var rig = new EnrollmentOrchestratorRig(At);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                rig.Build(agentMaxLifetime: TimeSpan.FromMilliseconds(milliseconds)));
        }

        [Fact]
        public void Stop_before_lifetime_elapses_silences_the_watchdog()
        {
            using var rig = new EnrollmentOrchestratorRig(At);
            var sut = rig.Build(agentMaxLifetime: TimeSpan.FromSeconds(5));

            var fireCount = 0;
            sut.Terminated += (_, _) => Interlocked.Increment(ref fireCount);

            sut.Start();
            Thread.Sleep(50);
            sut.Stop();

            // Give any racing timer callback a chance — it must not fire after Stop().
            Thread.Sleep(200);
            Assert.Equal(0, fireCount);
        }
    }
}
