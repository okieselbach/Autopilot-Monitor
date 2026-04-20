using System;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests
{
    public sealed class DecisionStateBuilderTests
    {
        [Fact]
        public void Build_withoutChanges_returnsEquivalentState()
        {
            var original = DecisionState.CreateInitial("s", "t");
            var rebuilt = original.ToBuilder().Build();

            Assert.NotSame(original, rebuilt);
            Assert.Equal(original.SessionId, rebuilt.SessionId);
            Assert.Equal(original.Stage, rebuilt.Stage);
            Assert.Equal(original.StepIndex, rebuilt.StepIndex);
            Assert.Equal(original.LastAppliedSignalOrdinal, rebuilt.LastAppliedSignalOrdinal);
        }

        [Fact]
        public void WithStage_returnsNewInstance_originalUnchanged()
        {
            var original = DecisionState.CreateInitial("s", "t");
            var updated = original.ToBuilder().WithStage(SessionStage.AwaitingHello).Build();

            Assert.Equal(SessionStage.SessionStarted, original.Stage);
            Assert.Equal(SessionStage.AwaitingHello, updated.Stage);
        }

        [Fact]
        public void AddDeadline_sameName_replacesExisting()
        {
            var original = DecisionState.CreateInitial("s", "t");
            var dl1 = new ActiveDeadline(
                name: "hello_safety",
                dueAtUtc: new DateTime(2026, 4, 20, 10, 5, 0, DateTimeKind.Utc),
                firesSignalKind: DecisionSignalKind.DeadlineFired);
            var dl2 = new ActiveDeadline(
                name: "hello_safety",
                dueAtUtc: new DateTime(2026, 4, 20, 10, 10, 0, DateTimeKind.Utc),
                firesSignalKind: DecisionSignalKind.DeadlineFired);

            var result = original.ToBuilder().AddDeadline(dl1).AddDeadline(dl2).Build();

            Assert.Single(result.Deadlines);
            Assert.Equal(dl2.DueAtUtc, result.Deadlines[0].DueAtUtc);
        }

        [Fact]
        public void CancelDeadline_removesByName()
        {
            var original = DecisionState.CreateInitial("s", "t");
            var dl = new ActiveDeadline(
                name: "hello_safety",
                dueAtUtc: DateTime.UtcNow.AddMinutes(5),
                firesSignalKind: DecisionSignalKind.DeadlineFired);

            var withDeadline = original.ToBuilder().AddDeadline(dl).Build();
            var cancelled = withDeadline.ToBuilder().CancelDeadline("hello_safety").Build();

            Assert.Single(withDeadline.Deadlines);
            Assert.Empty(cancelled.Deadlines);
        }

        [Fact]
        public void WithCurrentEnrollmentPhase_stampsSourceOrdinal()
        {
            var original = DecisionState.CreateInitial("s", "t");
            var updated = original.ToBuilder()
                .WithCurrentEnrollmentPhase(EnrollmentPhase.AccountSetup, sourceSignalOrdinal: 7)
                .Build();

            Assert.Equal(EnrollmentPhase.AccountSetup, updated.CurrentEnrollmentPhase!.Value);
            Assert.Equal(7, updated.CurrentEnrollmentPhase!.SourceSignalOrdinal);
        }
    }
}
